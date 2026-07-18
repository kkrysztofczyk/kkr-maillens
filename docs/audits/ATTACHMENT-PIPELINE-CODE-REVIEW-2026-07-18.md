# Code review — pipeline załączników Gmail

> Raport historyczny dla zakresu `f4f6b02..01351b8`. Nie opisuje bieżącego stanu
> `main`. Aktualny status ustaleń znajduje się w [indeksie audytów](README.md).

**Data:** 2026-07-18
**Zakres:** commity `f4f6b02..01351b8` (4 commity: model załączników, kolejka zadań, pobieranie załączników Gmail, zaszyfrowany magazyn blobów) + kontekst integracji.
**Weryfikacja:** testy jednostkowe **31/31 przeszły** (binarka aktualna względem HEAD). Uwaga: `dotnet build` nie działa lokalnie — `global.json` wymaga SDK 9.0.316, zainstalowany jest 9.0.102 (patrz punkt 7).

## Podsumowanie

Solidna, dobrze przetestowana warstwa infrastruktury: parametryzowane SQL wszędzie, klucze obce włączone (`Db.cs:20`), migracje transakcyjne z walidacją ciągłości wersji, AES-GCM z derywacją klucza per-domena, ochrona przed path traversal w magazynie blobów, deduplikacja po SHA-256, sensowne indeksy (w tym częściowy indeks unikalny na aktywne zadania). Znalazłem **1 istotny problem współbieżności** w kolejce zadań, 2 mniejsze problemy współbieżności/deduplikacji oraz kilka drobiazgów i luk w pokryciu testowym.

Pipeline jest na razie niedomknięty: `GmailSynchronizer` zapisuje metadane załączników, ale nic jeszcze nie enqueue'uje zadań `download` ani nie konsumuje kolejki — `EncryptedBlobStore`, `GmailAttachmentDownloader` i `ProcessingJobRepository` nie są używane w ścieżkach produkcyjnych (tylko w testach). Zakładam, że to następny etap.

---

## Istotne

### 1. `Complete`/`Fail` bez strażnika lease — możliwe podwójne przetworzenie i cofnięcie ukończonego zadania

`ProcessingJobRepository.cs:63-84` — oba UPDATE-y filtrują tylko po `WHERE id=$id`, bez sprawdzenia `locked_by` i `status`. Scenariusz awarii:

1. Worker A leasuje zadanie (lease 5 min) i zawiesza się na 10 min.
2. `RecoverExpired` zwraca zadanie do `pending`, worker B je leasuje i przetwarza.
3. Worker A „odwiesza się" i woła `Complete(id)` → zadanie oznaczone `completed`, choć B wciąż pracuje; albo woła `Fail(id, ...)` → status wraca do `pending` i zadanie zostanie przetworzone trzeci raz. `Fail` potrafi też cofnąć do `pending` zadanie, które B już ukończył.

**Poprawka:** przekazywać `workerId` do `Complete`/`Fail` i dodać do warunku `AND locked_by=$worker AND status='running'` (fencing). Wynik `ExecuteNonQuery()==0` oznacza „straciłem lease — porzuć wynik".

### 2. Wyścig `File.Move` w `EncryptedBlobStore.Put`

`EncryptedBlobStore.cs:44` — `if (!File.Exists(destination)) File.Move(temporary, destination);` to klasyczny TOCTOU: dwa równoległe `Put` tej samej treści mogą oba przejść test i drugi `File.Move` rzuci `IOException`, mimo że blob jest poprawnie zapisany. Poprawka: `try { File.Move(...) } catch (IOException) when (File.Exists(destination)) { }`.

### 3. Brak deduplikacji zadań bez `attachment_id`

Częściowy indeks unikalny `ux_processing_jobs_active_attachment` (`Migration005ProcessingJobs.cs:35-37`) obejmuje tylko wiersze z `attachment_id IS NOT NULL`. Dla przyszłych zadań dokumentowych (`document_id`) `INSERT OR IGNORE` w `Enqueue` niczego nie deduplikuje — duplikaty wejdą bez błędu. Jeśli zadania dokumentowe są planowane, potrzebny analogiczny indeks na `(job_type,document_id)`.

---

## Średnie

### 4. Bajt wersji formatu poza AAD

`EncryptedBlobStore.cs:96` — AAD w AES-GCM to tylko `Magic`; bajt wersji z nagłówka pliku nie jest uwierzytelniony. Dziś bez skutku (istnieje tylko wersja 1, inne odrzuca `Decrypt`), ale przy wprowadzeniu wersji 2 umożliwi mieszanie wersji nagłówka z cudzym szyfrogramem. Warto od razu włączyć `Magic || Version` do AAD.

### 5. Zapis strony synchronizacji w trzech osobnych transakcjach

`GmailSynchronizer.cs:176-183` — `GmailRepository.SaveMessages`, `Corpus.Upsert` i `MailAttachmentRepository.UpsertGmail` commitują niezależnie. Crash pomiędzy zostawia maile bez wierszy załączników (lub odwrotnie) do czasu ponownej synchronizacji tych wiadomości. Niska szkodliwość (resync naprawia stan), ale gdy dojdzie enqueue zadań, warto rozważyć jedną transakcję na stronę.

### 6. Ścisła walidacja rozmiaru może trwale blokować pobranie

`GmailAttachmentDownloader.cs:27-28` — `bytes.LongLength != attachment.SizeBytes` → wyjątek. Metadane `body.size` z Gmail API są zwykle dokładne, ale jakakolwiek rozbieżność (np. po stronie API) sprawi, że zadanie będzie failować deterministycznie aż do `max_attempts` i załącznik nigdy się nie pobierze. Skoro i tak liczony jest SHA-256, rozbieżność rozmiaru można logować zamiast rzucać — albo zostawić, świadomie preferując paranoję (wtedy warte komentarza).

### 7. Build nie działa na tej maszynie — rozjazd SDK

`global.json` wymaga SDK `9.0.316` (`rollForward: latestPatch`), zainstalowany jest `9.0.102`. `dotnet build/test` w katalogu repo od razu odmawia. Do decyzji: doinstalować SDK 9.0.316 albo poluzować `rollForward` na `latestFeature`.

---

## Drobne / upraszczające

- **Duplikacja `DecodeBase64Url`** — niemal identyczne implementacje w `GmailMessageMapper.cs:82-88` i `GmailAttachmentDownloader.cs:34-41` (różnią się tylko opakowaniem wyjątku). Skonsolidować do jednej.
- **Ręczny HKDF** — `EncryptedBlobStore.DeriveKey` (`EncryptedBlobStore.cs:128-138`) ręcznie składa extract+expand. .NET ma gotowe `HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info)` — mniej kodu własnej kryptografii.
- **Zbędne `ZeroMemory` szyfrogramu** — `EncryptedBlobStore.cs:102` zeruje kopię szyfrogramu; szyfrogram nie jest tajny (i tak ląduje na dysku). Kosmetyka.
- **`RecoverExpired` nadpisuje `error_code`** — `ProcessingJobRepository.cs:93` zastępuje pierwotną przyczynę błędu `lease-expired` przy każdym odzyskaniu; ginie informacja diagnostyczna o ostatnim realnym błędzie.
- **`inline_base64_data` w bazie** — inline'owe bajty trafiają jako base64 do TEXT w `mail_attachments` i są nadpisywane przy każdym upsercie. Baza jest SQLCipher, więc bez ryzyka bezpieczeństwa, ale to duplikacja danych względem przyszłego blob store'a — rozważyć czyszczenie kolumny po zmaterializowaniu blobu.

## Luki w pokryciu testowym

- `ProcessingJobRepository.Complete` i `Fail` nie mają żadnego testu (w tym ścieżki retry → `failed` po wyczerpaniu prób). To dokładnie obszar problemu nr 1.
- `GmailSynchronizerTests` nie weryfikują, że synchronizacja zapisuje `mail_attachments` — jedyna produkcyjna integracja nowego kodu (`GmailSynchronizer.cs:182`) jest nieprzetestowana end-to-end.
- Brak testu ochrony przed path traversal w `EncryptedBlobStore.Absolute` (zabezpieczenie istnieje, test by je utrwalił).

## Obserwacje (roadmapa, nie wady)

- **Brak GC blobów:** inwalidacja ustawia `blob_id=NULL`, a `is_deleted=1` nie usuwa plików — osierocone zaszyfrowane bloby będą się kumulować. Do zaplanowania sprzątanie po licznikach referencji (`ix_mail_attachments_blob` już jest pod to gotowy).
- **Brak producenta/konsumenta kolejki:** nic nie woła `Enqueue` po synchronizacji i nie istnieje worker przetwarzający zadania — spodziewany następny krok.

## Co się podoba

- Konsekwentnie parametryzowane SQL, zero konkatenacji — brak wektorów SQL injection.
- `PRAGMA foreign_keys=ON` + `ON DELETE CASCADE` — spójność referencyjna faktycznie egzekwowana.
- Wzorzec temp-file + move + deterministyczna ścieżka po hashu w blob store — odporny na częściowe zapisy.
- Lease z `RecoverExpired` w jednej transakcji `immediate` (`BeginTransaction(deferred: false)`) — poprawny pojedynczy pisarz SQLite.
- Migrator waliduje ciągłość wersji i odmawia pracy na nowszym schemacie.
- Nowe komponenty od razu z testami (dedup, złe klucze, limity rozmiaru, wygasanie lease).

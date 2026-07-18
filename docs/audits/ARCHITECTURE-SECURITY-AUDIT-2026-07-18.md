# KKR MailLens — audyt i weryfikacja

> Raport historyczny dla commitu `7f50d23`. Nie opisuje bieżącego stanu `main`.
> Aktualny status ustaleń znajduje się w [indeksie audytów](README.md).

**Data:** 2026-07-18
**Migawka:** commit `7f50d23` (*feat: persist extracted documents and segments*)
**Zakres:** przegląd architektury + bezpieczeństwa, weryfikacja budowania i testów.
**Status:** dokument ustaleń. **Nie wprowadzono żadnych zmian w kodzie.**

> **Zastrzeżenie metodologiczne.** W trakcie audytu drzewo robocze było aktywnie
> przebudowywane przez równoległy proces (pliki wędrowały między `src/KKR.MailLens`
> a `src/KKR.MailLens.Core`). Dlatego ustalenia opisują **stan z commitu `7f50d23`**,
> czytany przez `git show`, a nie z drzewa roboczego. Trzy pliki miały wtedy
> niezacommitowane zmiany: `EncryptedBlobStore.cs`, `MailAttachmentRepository.cs`,
> `Worker/Program.cs` — dla nich opis może odbiegać od bieżącej wersji.

---

## 1. Wyniki weryfikacji

| Co | Wynik |
|---|---|
| `dotnet build KKR.MailLens.sln -c Release` | **OK** — 0 błędów, 0 ostrzeżeń |
| `dotnet test` (`KKR.MailLens.Tests`) | **OK** — 20/20 przechodzi (~4 s) |
| SDK | 9.0.316 (zgodny z `global.json`), obok 10.0.302 |
| Hipoteza o zepsutych transakcjach | **OBALONA** — szczegóły niżej |

### 1.1. Sprostowanie: transakcje w `Corpus.Upsert` / `Reclassify`

We wstępnej analizie zgłosiłem podejrzenie, że `Corpus.Upsert` i `Corpus.Reclassify`
są zepsute w runtime, bo wołają `BeginTransaction()`, a następnie wykonują komendy
**bez** ustawienia `cmd.Transaction = tx` — co w `Microsoft.Data.Sqlite` bywa
raportowane jako `InvalidOperationException` („TransactionRequired").

**To był fałszywy alarm.** Zweryfikowano empirycznie: odtworzono dokładnie ten wzorzec
na `Microsoft.Data.Sqlite.Core` 9.0.0 + `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.10
(sonda w katalogu tymczasowym, poza repo). Wynik: **brak wyjątku**, transakcja commituje
się poprawnie. Biblioteka w tej wersji toleruje komendy bez jawnie przypisanej transakcji.

**Wniosek:** kod jest poprawny, nie wymaga zmiany. Pozostawiam jako notatkę, bo to
nieoczywiste zachowanie — gdyby kiedyś podbijać wersję `Microsoft.Data.Sqlite`,
warto ten wzorzec przetestować ponownie.

---

## 2. Architektura (stan na `7f50d23`)

Projekt urósł z „dwóch projektów" do **pięciu**:

| Projekt | Rola |
|---|---|
| `KKR.MailLens.Core` | Wspólny rdzeń (49 plików): krypto, DB, migracje, import, ekstrakcja |
| `KKR.MailLens` | CLI (`Cli.cs`, `Program.cs`, `SelfTest.cs`) |
| `KKR.MailLens.Gui` | WinForms — trzyma klucz sesji w RAM, serwer named-pipe |
| `KKR.MailLens.Worker` | **Nowy** — odizolowany proces przetwarzania zadań |
| `KKR.MailLens.Tests` | MSTest, 20 testów (obecnie tylko Gmail) |

### Potok przetwarzania

```
Gmail / IMAP / Outlook(COM)
        ↓  import
   korpus SQLCipher (mails + FTS5)
        ↓  kolejka zadań (processing_jobs)
   [download] → EncryptedBlobStore (AES-GCM, content-addressed)
        ↓
   [extract]  → PdfPig / OpenXml / text / HTML → content_documents + segmenty
        ↓
   [ocr]      → Tesseract (proces zewnętrzny, timeout)
```

Kolejka ma dzierżawy (`LeaseNext`, 5 min), ponawianie, licznik prób i `MarkFailed`
po przekroczeniu `MaxAttempts`. Worker działa jako osobny proces i pobiera klucz sesji
z GUI przez named-pipe; przerywa, gdy sesja przestaje być odblokowana.

### Migracje

Doraźne `EnsureSchema` zastąpiono wersjonowanym frameworkiem: `DatabaseMigrator`
+ `Migration001InitialCorpus` … `Migration007ContentDocuments`. To duży krok naprzód
względem pierwotnego inline'owego `CREATE TABLE IF NOT EXISTS`.

---

## 3. Ustalenia bezpieczeństwa

### 🔴 P1 — Poświadczenia do żywych kont chronione wyłącznie DPAPI

**Gdzie:** `GmailTokenStore.cs` (refresh token OAuth), `ImapAccounts.cs` (hasła IMAP).

Korpus na dysku wymaga PIN-u (+ YubiKey). Ale **refresh token Gmaila i hasła IMAP
są chronione tylko DPAPI `CurrentUser`** — czyli kontem Windows, nie kluczem sesji.

**Konsekwencja:** token daje pełny odczyt *żywej skrzynki Gmail* i jest odzyskiwalny
przez dowolny kod działający jako ten użytkownik — **również wtedy, gdy korpus jest
zablokowany, a GUI wyłączone**. Najsłabszym ogniwem przestaje być zaszyfrowana baza:
napastnik nie musi jej łamać, bo może po prostu wejść do oryginalnej skrzynki.

To odwraca deklarowany model bezpieczeństwa („bez PIN-u korpus jest nieodczytywalny").

**Kierunek naprawy (do decyzji, nie wdrożone):** owinąć tokeny/hasła kluczem sesji
(jak `EncryptedBlobStore`), tak by były użyteczne **tylko przy odblokowanej sesji**;
DPAPI zostawić jako warstwę dodatkową, nie jedyną.

### 🟠 P2 — `GETKEY` oddaje surowy klucz każdemu procesowi użytkownika

**Gdzie:** `Ipc.cs`, `Gui/Agent.cs`.

Pipe autoryzuje wyłącznie po SID — nie uwierzytelnia *klienta*. Przy odblokowanej sesji
dowolny proces działający jako Ty pobiera pełny klucz korpusu. Znaczenie wzrosło, bo
z tego samego klucza wyprowadzany jest klucz `EncryptedBlobStore` — czyli również
wszystkie pobrane załączniki.

Gwarancja „klucz nigdy nie trafia na dysk" pozostaje prawdziwa i chroni dane
w spoczynku oraz skradziony wyłączony dysk. Nie chroni przed lokalnym malware'em
przy aktywnej sesji. **To trzeba nazwać wprost w dokumentacji użytkownika.**

### 🟠 P2 — Nowa powierzchnia: parsowanie niezaufanych dokumentów

**Gdzie:** `Extraction/*`, zależności `PdfPig` 0.1.14, `DocumentFormat.OpenXml` 3.5.1,
oraz OCR przez Tesseract.

Załączniki z poczty to z definicji wrogie wejście, a są parsowane bibliotekami
o historii CVE w tej klasie. **Plus:** worker jest już osobnym procesem, a OCR ma
clamp na timeout (10 s – 1 h) — izolacja została rozpoczęta świadomie.
**Do przemyślenia:** limity pamięci/rozmiaru wejścia, twarde ograniczenie uprawnień
procesu worker, zachowanie przy „zip bombach" i głęboko zagnieżdżonym OOXML.

### 🟡 P3 — Entropia PIN-u w trybie bez YubiKey

`salt.bin` leży na dysku obok bazy. Przy 4–6-cyfrowym PIN-ie 200 000 iteracji PBKDF2
nie chroni przed atakiem offline na GPU. Realne zabezpieczenie daje dopiero YubiKey.
**Kierunek:** wymuszać passphrase (nie PIN) w trybie bez klucza sprzętowego albo
uczynić YubiKey obowiązkowym.

### 🟡 P3 — Statyczny challenge YubiKey, bez wymogu dotyku

Challenge = sól, więc odpowiedź jest stałym sekretem dla danej bazy, a odblokowanie
wymaga tylko „klucz wpięty + PIN", bez gestu. Udokumentowany kompromis (stare fw
YubiKey 4), ale warto go zapisać w modelu zagrożeń: krótki fizyczny dostęp do wpiętego
klucza + znajomość PIN-u = odblokowanie.

---

## 4. Co jest zrobione dobrze

- **`EncryptedBlobStore`** — AES-GCM, losowy nonce per blob, magic+wersja jako AAD,
  wyprowadzanie klucza w stylu HKDF z kontekstem, **kontrola path-traversal**,
  weryfikacja SHA-256 po odszyfrowaniu (`FixedTimeEquals`), zapis atomowy
  (temp + move), zeroizacja buforów, deduplikacja content-addressed. Solidne.
- **`GmailTokenStore`** — DPAPI z dodatkową entropią, zapis atomowy, zeroizacja
  plaintextu, nazwy plików jako skrót SHA-256.
- **OAuth zrobiony poprawnie** — `usePkce: true`, scope wyłącznie `GmailReadonly`,
  `client_secret` **nie jest** zaszyty w kodzie (env albo lokalny plik Desktop-app),
  `RemoveTokenAsync` próbuje realnie odwołać token po stronie Google.
- **`Setup.Init`** — walidacja przed destrukcją, budowa bazy w pliku tymczasowym
  i dopiero potem swap. Nieudany init nie osieroci istniejącego korpusu.
- **Outlook COM** — dedykowany wątek STA, konsekwentne `Marshal.ReleaseComObject`.
- **Framework migracji** zamiast doraźnego `EnsureSchema`.
- **SQL wszędzie parametryzowany**; `FtsSanitize` chroni przed błędami składni FTS5.

---

## 5. Do zrobienia

Kolejność wg priorytetu. **Żadna pozycja nie jest wdrożona.**

1. **[P1]** Przeprojektować ochronę refresh tokenu Gmail i haseł IMAP — powiązać
   z kluczem sesji, nie tylko z DPAPI.
2. **[P2]** Doprecyzować model zagrożeń w `README.md` / `docs.html`: wprost napisać,
   przed czym projekt chroni (skradziony dysk, dane w spoczynku), a przed czym **nie**
   (lokalny malware przy odblokowanej sesji, dostęp przez `GETKEY`).
3. **[P2]** Utwardzić przetwarzanie załączników: limity rozmiaru/pamięci, ograniczenie
   uprawnień procesu worker, testy na złośliwe PDF/OOXML.
4. **[P2]** Rozszerzyć testy poza Gmail — brak pokrycia dla `Corpus.Upsert`,
   `Query`, `NoiseRules`, migracji i `EncryptedBlobStore`. To najbardziej newralgiczne
   ścieżki, a są nieprzetestowane.
5. **[P3]** Zdecydować o polityce PIN-u (passphrase albo YubiKey obowiązkowy).
6. **[P3]** Zsynchronizować `README.md` z rzeczywistością — instruuje uruchamianie
   `run\KKR.MailLens.exe`, a katalog `run/` zawierał artefakty pod starą nazwą
   (plik wykonywalny pod starą nazwą). `run/` jest w `.gitignore`, więc to lokalne śmieci,
   ale instrukcja wprowadza w błąd. README nie wspomina też o Gmailu, workerze,
   OCR ani kolejce zadań.
7. **[P3]** Rozważyć użycie wbudowanego `System.Security.Cryptography.HKDF`
   zamiast ręcznej implementacji extract/expand w `EncryptedBlobStore.DeriveKey`
   (obecna jest poprawna — to kwestia utrzymania, nie błąd).
8. **[P3]** Przejrzeć puste `catch {}` pod kątem miejsc, gdzie cicho maskują realne
   błędy (np. pomijanie folderów w harveście bez śladu w logu).

---

## 6. Ocena ogólna

Kod jest **wyraźnie powyżej średniej** dla narzędzia tej klasy: brak własnej
kryptografii, standardowe prymitywy użyte poprawnie, dbałość o atomowość zapisów,
zeroizację i przypadki brzegowe, sensowny podział na projekty i realna izolacja
procesu przetwarzającego niezaufane dane.

Główny problem nie leży w jakości implementacji, lecz w **spójności modelu
zagrożeń**: fortyfikacja wokół korpusu jest mocna, natomiast poświadczenia
otwierające oryginalne skrzynki chronione są znacznie słabiej. To pozycja nr 1
na liście powyżej.

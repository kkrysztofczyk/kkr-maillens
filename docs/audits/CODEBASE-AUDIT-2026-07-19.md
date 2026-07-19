# Audyt całościowy kodu — 2026-07-19

Metoda: dwa przebiegi — (1) code review ostatnich 5 commitów, (2) całościowy audyt
repo w 9 równoległych kątach (Gmail, IMAP/Outlook, storage/krypto, ekstrakcja,
worker/kolejka, wyszukiwanie, GUI/CLI, przekrojowy security sweep, luki testowe).
Każdy kandydat poprawnościowy/bezpieczeństwa przeszedł osobną weryfikację
(CONFIRMED / PLAUSIBLE / REFUTED); pozycje REFUTED odrzucono.

Poniżej rejestr ustaleń i status ich naprawy. Zgodnie z przyjętym trybem pracy
poprawki realizowano jako osobne zadania (gałęzie `claude/*`), nie edytując kodu
inline w sesji audytowej; kolejne gałęzie były scalane do `main` niezależnie.

## Zdrowie bazowe (potwierdzone jako solidne)

- SQL w pełni parametryzowany, także FTS5 `MATCH`.
- Argumenty procesów zewnętrznych budowane przez `ArgumentList` (bez interpolacji powłoki).
- Nazwy plików z poczty neutralizowane (`Path.GetFileName` + allow-lista rozszerzeń), bloby po hashu.
- Endpoint embeddingów: `IsLoopback` + `UseProxy=false`.
- Klucz sesji przez ACL-owany named pipe (SID bieżącego użytkownika), nie w linii poleceń.
- Dokumentacja (`docs/audits/README.md` „Status bieżący") zgodna z kodem — zweryfikowano
  kluczowe deklaracje (loopback embeddingów, gate restricted-token, gwarancje GC).

## Ustalenia i status

Legenda: **zintegrowane** = na `main` (przez scalenie gałęzi naprawczej); **odrzucone**
= REFUTED przy weryfikacji.

### Code review ostatnich 5 commitów (semantic search, fallbacki OCR/whisper)

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 1 | TesseractOcr.cs | Wyjątek z fallbacku PaddleOCR unieważnia pusty wynik Tesseracta (regresja) | CONFIRMED | zintegrowane |
| 2 | PaddleOcr.cs / TesseractOcr.cs | Brak wymuszenia UTF-8 na potoku C↔Python → mojibake | CONFIRMED | zintegrowane |
| 3 | PaddleOcr.cs | Limit 16 KB stderr rzuca wyjątek przy udanym przebiegu | PLAUSIBLE | zintegrowane (persistent runner truncate) |
| 4 | Worker/Program.cs | Lease OCR = max() zamiast sumy przy sekwencyjnym fallbacku | PLAUSIBLE | zintegrowane (ProcessingLeaseMonitor) |
| 5 | SemanticSearch.cs | Niezgodność wymiarów embeddingów → mylący komunikat, puste wyniki | CONFIRMED | zintegrowane |
| 6 | SemanticSearch.cs | Kandydaci obcinani po dacie przed rankingiem; full-sort zamiast top-K | PLAUSIBLE | zintegrowane (streaming top-K) |
| — | SemanticSearch.cs | Triplikacja `Normalize`, podwójna normalizacja, `LimitInput` vs `TextLimit.Take` | cleanup | zintegrowane (dedupe vector math) |
| — | PaddleOcr/Tesseract | Duplikacja helperów procesowych (TryKill/Diagnostic/Limit/ComSpec) | cleanup | zintegrowane (ProcessRunner) |
| — | paddleocr_runner.py | Model ładowany per spawn; proces per strona | efficiency | zintegrowane (persistent runner) |

### Utrata danych i obsługa błędów — Gmail

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 7 | GmailSynchronizer.cs | historyId przesuwany mimo nieudanych pobrań → trwała utrata wiadomości | CONFIRMED | zintegrowane |
| 8 | GmailSynchronizer.cs | Pełny re-crawl kasuje wiadomości po przejściowym błędzie pobrania | CONFIRMED | zintegrowane |
| 9 | GmailSynchronizer.cs | Timeout HTTP klasyfikowany jak anulowanie użytkownika → abort całego synca | CONFIRMED | zintegrowane |
| 10 | GmailCancellation.cs | Globalna flaga anulowania kasuje wszystkie równoległe synci | PLAUSIBLE | zintegrowane |
| 11 | GmailMessageMapper.cs | `DecodePartText` łyka wyjątek → pusty body bez śladu w sync_errors | PLAUSIBLE | zintegrowane |
| 12 | GmailMessageMapper.cs | `ParseHeaderDate` bez AssumeUniversal; nagłówek preferowany nad internalDate | PLAUSIBLE | zintegrowane |

### Integralność blob store

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 13 | Worker/Program.cs | GC może skasować świeżo pobrany blob przed dowiązaniem załącznika | PLAUSIBLE | zintegrowane (okno bezpieczeństwa) |
| 14 | EncryptedBlobStore.cs | Re-upsert v1→v2 nie aktualizuje `encryption_version` → blob nieodczytywalny | PLAUSIBLE | zintegrowane |
| 15 | BlobGarbageCollector.cs | Plik kasowany przed wierszem DB → niespójność przy rollbacku | PLAUSIBLE | zintegrowane (wiersz przed plikiem) |
| 16 | Migration004MailAttachments.cs | Brak FK `blob_id`; wiszące referencje cicho przechodzą | PLAUSIBLE | zintegrowane (Migration012 triggery) |

### Worker / kolejka

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 17 | WorkerProcessLimit.cs | Anulowanie z GUI twardo ubija workera (KILL_ON_JOB_CLOSE); job wisi „running" | CONFIRMED | zintegrowane (graceful stop) |
| 18 | Worker/Program.cs | Branche download/extract bez odnawiania 5-min lease → podwójne przetwarzanie | PLAUSIBLE | zintegrowane |
| 19 | Worker/Program.cs | Lease transkrypcji do ~48 h; twardy crash blokuje job na cały lease | PLAUSIBLE | zintegrowane |
| 20 | ProcessingJobRepository.cs | `RetryFailed` może naruszyć unikalny indeks i nie przywrócić niczego | PLAUSIBLE | zintegrowane |
| 21 | Worker/Program.cs | 3 pominięcia STATUS (500 ms) → fałszywy „lock" i wyłączenie workera | PLAUSIBLE | zintegrowane |

### Wrogi content (bezpieczeństwo)

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 22 | GmailMessageMapper.cs | Regex-DoS w HtmlToText na wrogim HTML; niedomknięty `<script>` przecieka do indeksu | PLAUSIBLE | zintegrowane |
| 23 | StandardDocumentExtractors.cs | Zip-bomb guard ufa zadeklarowanym rozmiarom (Open nie ogranicza dekompresji) | PLAUSIBLE | zintegrowane |
| 24 | FileTypeDetector.cs | `archive.Entries` materializuje całe central directory przed limitem | PLAUSIBLE | zintegrowane |
| 25 | GoogleGmailApiClient.cs | Rekurencja MIME bez limitu głębokości → StackOverflow | PLAUSIBLE | zintegrowane |

### Ekstrakcja

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 26 | StandardDocumentExtractors.cs | `GetPartById` rzuca na nieznane r:id → crash zamiast pominięcia | PLAUSIBLE | zintegrowane |
| 27 | FileTypeDetector.cs | CSV/JSON/XML mapowane na MIME bez ekstraktora → treść nieindeksowana | PLAUSIBLE | zintegrowane |
| 28 | TextContentExtractors.cs | UTF-16 bez BOM dekodowane jak UTF-8 → śmieci w indeksie | PLAUSIBLE | zintegrowane |
| 29 | StandardDocumentExtractors.cs | Kwadratowe wyszukiwanie shared strings w Excelu | PLAUSIBLE | zintegrowane |
| 30 | MediaTranscription.cs | Wczesne wyjście ffmpeg maskuje diagnostykę stderr | PLAUSIBLE | zintegrowane |

### Zapytania / strefy czasowe

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 31 | Query.cs + źródła | `received` miesza UTC (Gmail) i czas lokalny (Outlook/IMAP) w jednej kolumnie | PLAUSIBLE | zintegrowane (normalizacja do UTC) |
| 32 | Query.cs | `--to` dokleja `' 99'` i porównuje leksykalnie; brak walidacji dat | PLAUSIBLE | zintegrowane |
| 33 | Query.cs | LIKE bez escapowania `%`/`_`; brak `ESCAPE` | PLAUSIBLE | zintegrowane |
| 34 | Query.cs | Fraza zaczynająca się od `--` psuje parsowanie argumentów | PLAUSIBLE | zintegrowane |

### Outlook / IMAP

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 35 | OutlookAttachmentDownloader.cs | Wymóg bajtowej równości z `PR_ATTACH_SIZE` (z narzutem) → fałszywe błędy | PLAUSIBLE | zintegrowane |
| 36 | Outlook.cs | Wycieki RCW COM w harvestingu (folder.Items, stores, root, subfoldery) | PLAUSIBLE | zintegrowane |
| 37 | Outlook.cs | Wyjątek `GetNamespace('MAPI')` osierocia `_app` / OUTLOOK.EXE | PLAUSIBLE | zintegrowane |
| 38 | MailAttachmentRepository.cs | Ta sama wiadomość w wielu folderach IMAP → locator last-writer-wins | PLAUSIBLE | zintegrowane |

### GUI / CLI

| # | Plik | Ustalenie | Werdykt | Status |
|---|------|-----------|---------|--------|
| 39 | MainForm.cs | Pola _search/_pin aktywne w trakcie pracy → nakładające się operacje, martwy Console.Out | PLAUSIBLE | zintegrowane |
| 40 | GmailManagerForm.cs | Zamknięcie formularza w trakcie async → ObjectDisposedException | PLAUSIBLE | zintegrowane |
| 41 | Cli.cs | Błędna wartość w `config` cicho ignorowana; ujemne wartości jako „unlimited" | PLAUSIBLE | zintegrowane |

### Odrzucone przy weryfikacji (REFUTED)

- **Walidacja rozmiaru załącznika Gmail** — poluzowanie do pasma tolerancji było *celową*
  poprawką z wcześniejszego audytu (`body.size` to szacunek); dokładne porównanie było bugiem.
- **Konstruktor fallbacku PaddleOCR wywala job** — runner jest kopiowany do katalogu wyjściowego
  i resolvowany przez `AppContext.BaseDirectory`, więc „nieznajdowalna ścieżka" nie zachodzi.
- **Stale flag anulowania blokuje kolejne synci** — flaga jest czyszczona na starcie każdego synca.
- **Osierocony worker po anulowaniu z GUI** — w rzeczywistości hard-kill (KILL_ON_JOB_CLOSE), nie sierota.

## Otwarte (do naprawy)

- **Restart pełnej synchronizacji Gmail po wygaśnięciu page tokenu** — patrz sekcja „Otwarte"
  w `docs/audits/README.md`. Test regresyjny istnieje i jest `[Ignore]`-owany do czasu naprawy.

## Kwestia do decyzji właściciela (nie zgłoszona jako bug)

Pipe IPC odpowiada `GETKEY` plaintext kluczem sesji każdemu procesowi tego samego
użytkownika przy odblokowanym GUI. To świadoma granica zaufania „ten sam użytkownik"
czy przeoczenie? Warto potwierdzić — reszta modelu danych-w-spoczynku jest spójna.

## Luki testowe (rejestr)

Krytyczne ścieżki bez pokrycia zidentyfikowane w przebiegu coverage; część dodano
w ramach zadań naprawczych (m.in. re-check TOCTOU blob-GC, dryf UIDVALIDITY,
wielostronicowa historia Gmaila, sanitizer FTS5, gate restricted-token, no-proxy
embeddingów). Nadal warto utrwalić: sekwencyjny fallback OCR dłuższy niż pojedyncza
dzierżawa vs. kadencja `ProcessingLeaseMonitor`. Restricted-worker test
(`Start_AttachesRestrictedProcessBeforeExecution`) pozostaje znanym failem w sandboxie
(STATUS_ACCESS_DENIED, exit `-1073741790`) — nie regresja.

# Architektura importu skrzynek

Ten dokument opisuje bieżący przepływ produkcyjny KKR MailLens. Historyczne raporty w `docs/audits` mogą opisywać starszy panel Gmail albo bezpośredni Harvest Outlooka.

## Model źródła

Każda skonfigurowana skrzynka ma rekord w `mailbox_sources`:

- `gmail` wskazuje konto OAuth przez referencję `gmail-account:<id>`;
- `imap` przechowuje neutralne ustawienia połączenia w bazie, a zaszyfrowane hasło w chronionym pliku kont;
- `outlook` zapisuje dokładny `StoreID` i migawkę podłączonego magazynu. Plik PST/OST musi być udostępniony przez Outlook; aplikacja go nie montuje.

Wiadomość otrzymuje `mailbox_source_id`. Dzięki temu dashboard może osobno liczyć dane i postęp każdej skrzynki bez zmiany istniejącej tożsamości wiadomości, SQLite, SQLCipher ani FTS5.

## Trwała kolejka

`mailbox_import_runs` reprezentuje cały przebieg, a `mailbox_import_run_sources` jego uporządkowane źródła. Kolejka zapisuje:

- kolejność skrzynek i migawkę ich ustawień;
- wybór importu pełnego lub przyrostowego;
- stan przebiegu: `queued`, `importing`, `processing` albo stan końcowy;
- postęp, liczniki, bezpieczny kod błędu i żądanie anulowania;
- identyfikator ostatniego zadania przetwarzania istniejącego przed utworzeniem przebiegu.

Tylko jeden przebieg może być aktywny. Importer uruchamia jedno źródło naraz. Błąd jednej skrzynki jest zapisywany, ale nie blokuje następnej. Nowe źródło można dopisać, dopóki przebieg jest w stanie `queued` albo `importing`.

Po restarcie źródło pozostawione w stanie `importing` wraca do kolejki. Importy są idempotentne, więc ponowienie zachowuje wcześniej zatwierdzone porcje i nie tworzy duplikatów.

## Przetwarzanie po imporcie

`MailboxPipelineCoordinator` łączy dwie fazy:

1. `MailboxImportCoordinator` pobiera kolejno Gmail, IMAP i Outlook.
2. `ProcessingCoordinator` uruchamia ograniczony `KKR.MailLens.Worker.exe --drain`.

Worker nadal używa istniejącego repozytorium `processing_jobs` i obsługuje `download`, `extract`, `ocr`, `transcribe` oraz `embed`. Koordynator nie zastępuje logiki Workera. Odczytuje tylko zadania utworzone po rozpoczęciu danego przebiegu i należące do jego skrzynek, dzięki czemu statystyki nie mieszają się ze starszą kolejką.

Zadanie oczekujące na retry nie powoduje aktywnego odpytywania w pętli. Koordynator czeka do `available_at` i ponownie uruchamia Workera. Wygasły lease może zostać odzyskany przez istniejący mechanizm `LeaseNext`.

Kod wyjścia `2` oznacza zablokowaną sesję i pozostawia przebieg w stanie `processing`, gotowy do wznowienia. Kod `130` albo jawne anulowanie kończą przebieg jako `cancelled`. Inny kod procesu lub awaria oczekiwania zapisują bezpieczny kod błędu i stan `failed`.

## GUI

Przycisk `Skrzynki` zastępuje dawne wejścia `Harvest` i `Gmail`. Panel:

- dodaje Gmail, IMAP i podłączone magazyny Outlook/PST;
- pokazuje liczbę skrzynek i wiadomości;
- pozwala uruchomić import wybranych albo wszystkich aktywnych źródeł;
- pozwala dopisywać źródła podczas fazy importu;
- pokazuje kolejkę źródeł oraz osobne liczniki pobierania, ekstrakcji, OCR, transkrypcji i indeksu;
- odzyskuje aktywny przebieg po ponownym otwarciu;
- anuluje przebieg po żądaniu użytkownika albo utracie aktywnej sesji.

CLI pozostaje zgodne wstecz. Polecenia `harvest`, `imap-harvest`, `gmail sync` i `processing-run` nadal służą do niezależnej automatyzacji i diagnostyki.

## Migracje

- `013` — wspólny rejestr `mailbox_sources`;
- `014` — przebiegi i uporządkowane źródła importu;
- `015` — punkt początkowy zadań przetwarzania;
- `016` — trwały wybór pełnego importu.

Migracje są addytywne. Nie zmieniają mechanizmu szyfrowania, danych korpusu, istniejącego indeksu wiadomości ani indeksowania treści załączników.

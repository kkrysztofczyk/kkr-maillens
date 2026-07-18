# KKR MailLens

KKR MailLens tworzy lokalny, szyfrowany indeks poczty do wyszukiwania pełnotekstowego i analityki. Dane są przechowywane w bazie SQLCipher, wyszukiwanie korzysta z FTS5, a klucz aktywnej sesji pozostaje w pamięci procesu GUI.

## Stan projektu

Aktualnie działa import Outlook/IMAP oraz pełna i przyrostowa synchronizacja Gmail API z CLI. Pipeline Gmail obejmuje trwałą kolejkę, pobieranie załączników, szyfrowany i deduplikowany magazyn blobów, ekstrakcję TXT/HTML/PDF/DOCX/XLSX/PPTX, lokalny OCR obrazów i mieszanych PDF-ów oraz osobny indeks `content_fts`.

Nie są jeszcze dostępne: obsługa Gmaila i wyników załączników w GUI, pobieranie załączników Outlook/IMAP, transkrypcja audio/wideo, garbage collection osieroconych blobów oraz wyszukiwanie semantyczne. Bieżący status ustaleń technicznych znajduje się w [indeksie audytów](docs/audits/README.md).

## Wymagania

- Windows
- .NET 9 Desktop Runtime do uruchomienia; SDK 9.0.316 do budowania i testów
- źródło desktopowe obsługiwane przez integrację COM, konto IMAP albo konto Gmail połączone przez OAuth 2.0
- opcjonalny sprzętowy drugi składnik uwierzytelnienia
- opcjonalnie Tesseract 5 z danymi językowymi `pol` i `eng` do lokalnego OCR obrazów i skanowanych PDF-ów

## Build

```powershell
dotnet build KKR.MailLens.sln
dotnet test KKR.MailLens.sln
dotnet publish src\KKR.MailLens\KKR.MailLens.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\KKR.MailLens.Gui\KKR.MailLens.Gui.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\KKR.MailLens.Worker\KKR.MailLens.Worker.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
```

Solution składa się z biblioteki `src\KKR.MailLens.Core`, aplikacji CLI `src\KKR.MailLens`, aplikacji WinForms `src\KKR.MailLens.Gui`, osobnego procesu `src\KKR.MailLens.Worker` oraz projektu testowego. CLI, GUI i Worker korzystają z tego samego rdzenia przez `ProjectReference`; nie linkują ręcznie plików źródłowych. Assembly wykonywalne nazywają się `KKR.MailLens`, `KKR.MailLens.Gui` i `KKR.MailLens.Worker`.

## Szybki start

1. Uruchom `run\KKR.MailLens.Gui.exe`.
2. Ustaw PIN i zainicjuj zaszyfrowaną bazę.
3. Odblokuj sesję w GUI.
4. Zaimportuj wiadomości przyciskiem `Harvest`, przez IMAP albo przez Gmail API.
5. Wyszukuj z GUI lub CLI.

```powershell
run\KKR.MailLens.exe status
run\KKR.MailLens.exe query "neutralny tekst"
run\KKR.MailLens.exe stats
```

Przykładowa konfiguracja IMAP używa wyłącznie zarezerwowanej domeny testowej; wartości trzeba zastąpić konfiguracją własnego serwera:

```powershell
run\KKR.MailLens.exe imap-add --host imap.example.invalid --user sender@example.invalid
run\KKR.MailLens.exe imap-harvest --account sender@example.invalid --since 2026-01-01
```

## Gmail API i OAuth 2.0

Integracja Gmail korzysta wyłącznie z oficjalnego Gmail API i zakresu `gmail.readonly`. Aplikacja otwiera logowanie w systemowej przeglądarce, nie przyjmuje hasła do Gmaila i nie uruchamia własnego serwera poza tymczasowym odbiornikiem OAuth na lokalnym adresie loopback.

Zarządzanie kontami i synchronizacja Gmail są obecnie dostępne w CLI. Dedykowany panel Gmail w GUI pozostaje na roadmapie.

Zakres `gmail.readonly` jest klasyfikowany jako restricted. Publicznie udostępniany klient OAuth może wymagać weryfikacji zgodnie z bieżącymi zasadami Google; repozytorium nie zawiera wspólnego identyfikatora ani sekretu klienta.

1. W konsoli dostawcy OAuth włącz Gmail API i utwórz klienta typu **Desktop app**.
2. Pobrany plik konfiguracji zapisz poza repozytorium jako `%LOCALAPPDATA%\kkr-maillens\gmail-oauth-client.json` albo wskaż go zmienną `KKR_MAILLENS_GMAIL_OAUTH_CONFIG`.
3. Uruchom GUI i odblokuj bazę, a następnie wykonaj:

```powershell
run\KKR.MailLens.exe account add gmail
run\KKR.MailLens.exe account list
run\KKR.MailLens.exe gmail sync
run\KKR.MailLens.exe gmail status
run\KKR.MailLens.exe processing-status
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
```

Pełną kontrolowaną synchronizację wymusza `gmail sync --full`. Działającą synchronizację można zatrzymać z drugiego terminala poleceniem `gmail cancel`. Przy wielu kontach służy parametr `--account <id|adres>`.

Pierwszy import jest stronicowany i zapamiętuje checkpoint. Kolejne uruchomienia korzystają z historii zmian Gmaila; po wygaśnięciu `historyId` aplikacja automatycznie wykonuje kontrolowany full sync bez duplikowania danych. Importowane są także wiadomości zarchiwizowane, etykiety, flagi unread/spam/trash oraz metadane załączników.

## Załączniki, ekstrakcja i OCR

Worker pobiera załączniki Gmaila do deduplikowanego magazynu szyfrowanego AES-GCM. Jawna zawartość jest odszyfrowywana wyłącznie w pamięci procesu. Obsługiwane ekstraktory deterministyczne obejmują TXT/CSV/XML/JSON, HTML, PDF z warstwą tekstową oraz DOCX/XLSX/PPTX. Segmenty zachowują odpowiednio numer strony, slajdu lub nazwę arkusza i trafiają do osobnego indeksu FTS5. Archiwa OpenXML podlegają limitom liczby wpisów, rozwiniętego rozmiaru i współczynnika kompresji; typy bez ekstraktora otrzymują status `skipped`.

OCR obrazów PNG/JPEG/TIFF/BMP oraz skanowanych stron PDF korzysta z lokalnego Tesseracta przez `stdin`/`stdout`, bez tworzenia jawnego pliku tymczasowego. PDF jest analizowany strona po stronie: zachowywany jest poprawny tekst istniejący, a przez PDFium renderowane są wyłącznie strony puste lub zawierające zbyt mało użytecznego tekstu. Obrazy PNG stron pozostają tylko w pamięci i są zerowane po OCR.

Domyślne języki to `pol+eng`, rozdzielczość PDF to 300 DPI, a limit jednego dokumentu wynosi 100 stron wymagających OCR. Ścieżkę, języki, timeouty i limity można ustawić poleceniem:

```powershell
run\KKR.MailLens.exe config --tesseract "C:\Program Files\Tesseract-OCR\tesseract.exe" --ocr-languages pol+eng --ocr-timeout 120 --ocr-pdf-dpi 300 --ocr-max-pdf-pages 100 --ocr-pdf-render-timeout 120
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
run\KKR.MailLens.exe rebuild-content-index
```

PDF bez użytecznej warstwy tekstowej na co najmniej jednej stronie otrzymuje status `needs-ocr`, po czym Worker automatycznie zleca OCR tych stron. Segmenty tekstowe i OCR są scalane według numeru strony i indeksowane w FTS5. Podczas długiego OCR Worker odnawia dzierżawę zadania po każdej stronie.

## Neutralne dane przykładowe

- temat: `Test Record`
- nadawca: `sender@example.invalid`
- odbiorca: `recipient@example.invalid`
- treść: `Neutralny tekst wiadomości używany do testowania indeksu`
- fraza wyszukiwania: `neutralny tekst`

## Dane i bezpieczeństwo

Domyślny katalog danych to `%LOCALAPPDATA%\kkr-maillens`. Lokalizację można zmienić zmienną `KKR_MAILLENS_DIR`.

Baza pozostaje szyfrowana przez SQLCipher. Klucz jest wyprowadzany z PIN-u i opcjonalnego drugiego składnika, a następnie przechowywany wyłącznie w RAM działającego GUI. Import jest idempotentny, SQLite zachowuje dotychczasowy schemat danych, a wyszukiwanie nadal korzysta z FTS5.

Refresh tokeny Gmaila i hasła IMAP są szyfrowane AES-GCM kluczem wyprowadzonym z aktywnej sesji, a zewnętrzna warstwa pliku jest dodatkowo chroniona przez Windows DPAPI `CurrentUser`. Starsze dane chronione wyłącznie DPAPI są automatycznie migrowane po poprawnym odblokowaniu. Tokeny nie są wypisywane w logach. Pliki klienta OAuth są ignorowane przez Git i nie wolno ich commitować. Treść wiadomości pozostaje lokalna; aplikacja komunikuje się wyłącznie z wybranym źródłem poczty.

Model chroni przede wszystkim dane w spoczynku, na przykład przy utracie wyłączonego komputera lub nośnika. Nie chroni przed złośliwym kodem uruchomionym jako ten sam użytkownik Windows podczas odblokowanej sesji: lokalny proces tego użytkownika może komunikować się z named pipe GUI i uzyskać dostęp do operacji wykonywanych przez odblokowaną aplikację. Ponowna inicjalizacja z `force` usuwa tokeny, hasła źródeł i bloby związane z poprzednim kluczem korpusu.

Historyczne raporty i aktualny status ich ustaleń są dostępne w [`docs/audits`](docs/audits/README.md).

## Polecenia

Pełną listę poleceń pokazuje:

```powershell
run\KKR.MailLens.exe help
```

Najważniejsze operacje to `init`, `status`, `lock`, `config`, `harvest`, `account`, `gmail`, `processing-run`, `processing-status`, `processing-retry`, `query`, `query-content`, `rebuild-content-index`, `stats`, `analyze`, `analyze-rules`, `reclassify` i `selftest`.

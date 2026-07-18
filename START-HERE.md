# KKR MailLens — szybki start

KKR MailLens to lokalny, szyfrowany indeks poczty z wyszukiwaniem FTS5. Klucz aktywnej sesji pozostaje wyłącznie w RAM aplikacji GUI.

## Uruchomienie GUI

1. Uruchom `run\KKR.MailLens.Gui.exe`.
2. Wpisz PIN, opcjonalnie włącz drugi składnik i kliknij `Inicjuj`.
3. Kliknij `Odblokuj`.
4. Kliknij `Harvest`, aby pobrać pocztę ze źródła desktopowego.
5. Użyj pola `Szukaj` i wybierz zakres `Wiadomości`, `Załączniki` albo `Wszystko`; alerty automatyczne są domyślnie odsiewane według lokalnych reguł.

`Harvest` zapisuje także metadane załączników Outlook. Uruchom `run\KKR.MailLens.exe processing-run`, aby Worker pobrał je przez dedykowany wątek STA, zaszyfrował i zindeksował. Jawny plik wymagany przez API Outlooka jest usuwany z izolowanego katalogu roboczego przed zakończeniem zadania.

## IMAP

Poniższe wartości są neutralnymi placeholderami z domeny `.invalid`:

```powershell
run\KKR.MailLens.exe imap-add --host imap.example.invalid --user sender@example.invalid
run\KKR.MailLens.exe imap-harvest --account sender@example.invalid --since 2026-01-01
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
```

Import IMAP zapisuje metadane i trwałe identyfikatory części MIME. Worker pobiera później wyłącznie załącznik, szyfruje go i przekazuje do ekstrakcji, OCR albo transkrypcji.

## Gmail przez OAuth

Włącz Gmail API i utwórz klienta OAuth typu Desktop app. Plik konfiguracji zapisz poza repozytorium jako `%LOCALAPPDATA%\kkr-maillens\gmail-oauth-client.json` albo wskaż zmienną `KKR_MAILLENS_GMAIL_OAUTH_CONFIG`. Po odblokowaniu GUI:

Kliknij `Gmail` w głównym oknie, aby połączyć lub odłączyć konto, wykonać synchronizację przyrostową albo pełną, obserwować postęp i kolejkę oraz uruchomić Workera. Te same operacje pozostają dostępne w CLI:

```powershell
run\KKR.MailLens.exe account add gmail
run\KKR.MailLens.exe gmail sync
run\KKR.MailLens.exe gmail status
run\KKR.MailLens.exe processing-status
run\KKR.MailLens.exe processing-run
```

Logowanie otwiera się w systemowej przeglądarce. Aplikacja nie pobiera hasła; refresh token jest szyfrowany kluczem aktywnej sesji i dodatkowo chroniony przez Windows DPAPI. Starsze tokeny są migrowane po odblokowaniu. `gmail sync --full` wymusza pełną synchronizację, a `gmail cancel` zatrzymuje trwającą operację.

## Wyszukiwanie z CLI

```powershell
run\KKR.MailLens.exe query "neutralny tekst"
run\KKR.MailLens.exe query-content "neutralny tekst"
run\KKR.MailLens.exe stats
run\KKR.MailLens.exe help
```

Neutralny rekord testowy ma temat `Test Record`, nadawcę `sender@example.invalid`, odbiorcę `recipient@example.invalid` i treść `Neutralny tekst wiadomości używany do testowania indeksu`.

## Załączniki i lokalny OCR

TXT/HTML/PDF/DOCX/XLSX/PPTX są ekstrahowane przez osobny proces Worker, a pliki źródłowe pozostają w zaszyfrowanym magazynie. Dla obrazów PNG/JPEG/TIFF/BMP oraz skanowanych PDF-ów można skonfigurować lokalny Tesseract 5:

```powershell
run\KKR.MailLens.exe config --tesseract "C:\Program Files\Tesseract-OCR\tesseract.exe" --ocr-languages pol+eng --ocr-pdf-dpi 300 --ocr-max-pdf-pages 100 --ocr-pdf-batch-size 4 --worker-memory-mb 1536
run\KKR.MailLens.exe processing-run
```

OCR przekazuje obrazy przez pamięć i strumienie procesu, bez jawnego pliku tymczasowego. Worker automatycznie renderuje przez PDFium tylko strony PDF bez użytecznej warstwy tekstowej, grupując je w ograniczone batche (domyślnie po 4), zeruje wykorzystane bufory PNG, scala wynik według numerów stron i aktualizuje indeks FTS5.
Uruchomienie przez `processing-run` nakłada ograniczony token Windows, izolację interfejsu Job Object oraz limit pamięci na Workera i procesy potomne. Ograniczenia są aktywne przed wznowieniem procesu, a Worker uruchomiony bez launchera odmawia pracy. Ctrl+C lub zablokowanie sesji bezpiecznie zwraca aktywne zadanie do kolejki.

## Lokalna transkrypcja

Po zainstalowaniu FFmpeg, `whisper-cli` i wielojęzycznego modelu whisper.cpp:

```powershell
run\KKR.MailLens.exe config --ffmpeg "C:\Tools\ffmpeg\bin\ffmpeg.exe" --whisper "C:\Tools\whisper.cpp\whisper-cli.exe" --whisper-model "C:\Models\ggml-small.bin" --whisper-language auto
run\KKR.MailLens.exe processing-run
```

Audio z nagrań i filmów jest lokalnie konwertowane do mono PCM 16 kHz. Segmenty transkrypcji zachowują zakres czasu i są dostępne przez `query-content`. Pliki robocze WAV/JSON są usuwane po zadaniu i nie trafiają do repozytorium.

Aktualny status funkcji oraz ustaleń bezpieczeństwa znajduje się w [indeksie audytów](docs/audits/README.md).

## Lokalizacja danych

Dane są przechowywane w `%LOCALAPPDATA%\kkr-maillens`. Alternatywny katalog można wskazać zmienną `KKR_MAILLENS_DIR`.

## Build i self-test

```powershell
dotnet build KKR.MailLens.sln
dotnet test KKR.MailLens.sln
dotnet run --project src\KKR.MailLens\KKR.MailLens.csproj -- selftest
dotnet publish src\KKR.MailLens\KKR.MailLens.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\KKR.MailLens.Gui\KKR.MailLens.Gui.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\KKR.MailLens.Worker\KKR.MailLens.Worker.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
```

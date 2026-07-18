# KKR MailLens — szybki start

KKR MailLens to lokalny, szyfrowany indeks poczty z wyszukiwaniem FTS5. Klucz aktywnej sesji pozostaje wyłącznie w RAM aplikacji GUI.

## Uruchomienie GUI

1. Uruchom `run\KKR.MailLens.Gui.exe`.
2. Wpisz PIN, opcjonalnie włącz drugi składnik i kliknij `Inicjuj`.
3. Kliknij `Odblokuj`.
4. Kliknij `Harvest`, aby pobrać pocztę ze źródła desktopowego.
5. Użyj pola `Szukaj`; alerty automatyczne są domyślnie odsiewane według lokalnych reguł.

## IMAP

Poniższe wartości są neutralnymi placeholderami z domeny `.invalid`:

```powershell
run\KKR.MailLens.exe imap-add --host imap.example.invalid --user sender@example.invalid
run\KKR.MailLens.exe imap-harvest --account sender@example.invalid --since 2026-01-01
```

## Gmail przez OAuth

Włącz Gmail API i utwórz klienta OAuth typu Desktop app. Plik konfiguracji zapisz poza repozytorium jako `%LOCALAPPDATA%\kkr-maillens\gmail-oauth-client.json` albo wskaż zmienną `KKR_MAILLENS_GMAIL_OAUTH_CONFIG`. Po odblokowaniu GUI:

```powershell
run\KKR.MailLens.exe account add gmail
run\KKR.MailLens.exe gmail sync
run\KKR.MailLens.exe gmail status
run\KKR.MailLens.exe processing-status
run\KKR.MailLens.exe processing-run
```

Logowanie otwiera się w systemowej przeglądarce. Aplikacja nie pobiera hasła; refresh token jest chroniony przez Windows DPAPI. `gmail sync --full` wymusza pełną synchronizację, a `gmail cancel` zatrzymuje trwającą operację.

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
run\KKR.MailLens.exe config --tesseract "C:\Program Files\Tesseract-OCR\tesseract.exe" --ocr-languages pol+eng --ocr-pdf-dpi 300 --ocr-max-pdf-pages 100
run\KKR.MailLens.exe processing-run
```

OCR przekazuje obrazy przez pamięć i strumienie procesu, bez jawnego pliku tymczasowego. Worker automatycznie renderuje przez PDFium tylko strony PDF bez użytecznej warstwy tekstowej, wykonuje OCR strona po stronie, scala wynik według numerów stron i aktualizuje indeks FTS5.

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

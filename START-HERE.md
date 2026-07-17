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

## Wyszukiwanie z CLI

```powershell
run\KKR.MailLens.exe query "neutralny tekst"
run\KKR.MailLens.exe stats
run\KKR.MailLens.exe help
```

Neutralny rekord testowy ma temat `Test Record`, nadawcę `sender@example.invalid`, odbiorcę `recipient@example.invalid` i treść `Neutralny tekst wiadomości używany do testowania indeksu`.

## Lokalizacja danych

Dane są przechowywane w `%LOCALAPPDATA%\kkr-maillens`. Alternatywny katalog można wskazać zmienną `KKR_MAILLENS_DIR`.

## Build i self-test

```powershell
dotnet build KKR.MailLens.sln
dotnet run --project src\KKR.MailLens\KKR.MailLens.csproj -- selftest
```

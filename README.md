# KKR MailLens

KKR MailLens tworzy lokalny, szyfrowany indeks poczty do wyszukiwania pełnotekstowego i analityki. Dane są przechowywane w bazie SQLCipher, wyszukiwanie korzysta z FTS5, a klucz aktywnej sesji pozostaje w pamięci procesu GUI.

## Wymagania

- Windows
- .NET 9 Desktop Runtime
- źródło desktopowe obsługiwane przez integrację COM lub konto IMAP
- opcjonalny sprzętowy drugi składnik uwierzytelnienia

## Build

```powershell
dotnet build KKR.MailLens.sln
```

Projekty znajdują się w `src\KKR.MailLens` i `src\KKR.MailLens.Gui`. Assembly oraz artefakty uruchomieniowe nazywają się odpowiednio `KKR.MailLens` i `KKR.MailLens.Gui`.

## Szybki start

1. Uruchom `run\KKR.MailLens.Gui.exe`.
2. Ustaw PIN i zainicjuj zaszyfrowaną bazę.
3. Odblokuj sesję w GUI.
4. Zaimportuj wiadomości przyciskiem `Harvest` albo przez skonfigurowane konto IMAP.
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

## Neutralne dane przykładowe

- temat: `Test Record`
- nadawca: `sender@example.invalid`
- odbiorca: `recipient@example.invalid`
- treść: `Neutralny tekst wiadomości używany do testowania indeksu`
- fraza wyszukiwania: `neutralny tekst`

## Dane i bezpieczeństwo

Domyślny katalog danych to `%LOCALAPPDATA%\kkr-maillens`. Lokalizację można zmienić zmienną `KKR_MAILLENS_DIR`.

Baza pozostaje szyfrowana przez SQLCipher. Klucz jest wyprowadzany z PIN-u i opcjonalnego drugiego składnika, a następnie przechowywany wyłącznie w RAM działającego GUI. Import jest idempotentny, SQLite zachowuje dotychczasowy schemat danych, a wyszukiwanie nadal korzysta z FTS5.

## Polecenia

Pełną listę poleceń pokazuje:

```powershell
run\KKR.MailLens.exe help
```

Najważniejsze operacje to `init`, `status`, `lock`, `config`, `harvest`, `imap-add`, `imap-list`, `imap-harvest`, `query`, `stats`, `analyze`, `analyze-rules`, `reclassify` i `selftest`.

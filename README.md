# KKR MailLens

KKR MailLens tworzy lokalny, szyfrowany indeks poczty do wyszukiwania pełnotekstowego i analityki. Dane są przechowywane w bazie SQLCipher, wyszukiwanie korzysta z FTS5, a klucz aktywnej sesji pozostaje w pamięci procesu GUI.

## Wymagania

- Windows
- .NET 9 Desktop Runtime do uruchomienia; SDK 9.0.316 do budowania i testów
- źródło desktopowe obsługiwane przez integrację COM, konto IMAP albo konto Gmail połączone przez OAuth 2.0
- opcjonalny sprzętowy drugi składnik uwierzytelnienia

## Build

```powershell
dotnet build KKR.MailLens.sln
dotnet test KKR.MailLens.sln
dotnet publish src\KKR.MailLens\KKR.MailLens.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
dotnet publish src\KKR.MailLens.Gui\KKR.MailLens.Gui.csproj -c Release -o run -p:DebugType=None -p:DebugSymbols=false
```

Projekty znajdują się w `src\KKR.MailLens` i `src\KKR.MailLens.Gui`. Assembly oraz artefakty uruchomieniowe nazywają się odpowiednio `KKR.MailLens` i `KKR.MailLens.Gui`.

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

Zakres `gmail.readonly` jest klasyfikowany jako restricted. Publicznie udostępniany klient OAuth może wymagać weryfikacji zgodnie z bieżącymi zasadami Google; repozytorium nie zawiera wspólnego identyfikatora ani sekretu klienta.

1. W konsoli dostawcy OAuth włącz Gmail API i utwórz klienta typu **Desktop app**.
2. Pobrany plik konfiguracji zapisz poza repozytorium jako `%LOCALAPPDATA%\kkr-maillens\gmail-oauth-client.json` albo wskaż go zmienną `KKR_MAILLENS_GMAIL_OAUTH_CONFIG`.
3. Uruchom GUI i odblokuj bazę, a następnie wykonaj:

```powershell
run\KKR.MailLens.exe account add gmail
run\KKR.MailLens.exe account list
run\KKR.MailLens.exe gmail sync
run\KKR.MailLens.exe gmail status
```

Pełną kontrolowaną synchronizację wymusza `gmail sync --full`. Działającą synchronizację można zatrzymać z drugiego terminala poleceniem `gmail cancel`. Przy wielu kontach służy parametr `--account <id|adres>`.

Pierwszy import jest stronicowany i zapamiętuje checkpoint. Kolejne uruchomienia korzystają z historii zmian Gmaila; po wygaśnięciu `historyId` aplikacja automatycznie wykonuje kontrolowany full sync bez duplikowania danych. Importowane są także wiadomości zarchiwizowane, etykiety, flagi unread/spam/trash oraz metadane załączników.

## Neutralne dane przykładowe

- temat: `Test Record`
- nadawca: `sender@example.invalid`
- odbiorca: `recipient@example.invalid`
- treść: `Neutralny tekst wiadomości używany do testowania indeksu`
- fraza wyszukiwania: `neutralny tekst`

## Dane i bezpieczeństwo

Domyślny katalog danych to `%LOCALAPPDATA%\kkr-maillens`. Lokalizację można zmienić zmienną `KKR_MAILLENS_DIR`.

Baza pozostaje szyfrowana przez SQLCipher. Klucz jest wyprowadzany z PIN-u i opcjonalnego drugiego składnika, a następnie przechowywany wyłącznie w RAM działającego GUI. Import jest idempotentny, SQLite zachowuje dotychczasowy schemat danych, a wyszukiwanie nadal korzysta z FTS5.

Refresh tokeny Gmaila są przechowywane poza bazą w plikach chronionych przez Windows DPAPI dla bieżącego użytkownika. Tokeny nie są wypisywane w logach. Pliki klienta OAuth są ignorowane przez Git i nie wolno ich commitować. Treść wiadomości pozostaje lokalna; aplikacja komunikuje się wyłącznie z wybranym źródłem poczty.

## Polecenia

Pełną listę poleceń pokazuje:

```powershell
run\KKR.MailLens.exe help
```

Najważniejsze operacje to `init`, `status`, `lock`, `config`, `harvest`, `account`, `gmail`, `imap-add`, `imap-list`, `imap-harvest`, `query`, `stats`, `analyze`, `analyze-rules`, `reclassify` i `selftest`.

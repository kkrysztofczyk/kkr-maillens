# KKR MailLens

KKR MailLens tworzy lokalny, szyfrowany indeks poczty do wyszukiwania pełnotekstowego i analityki. Dane są przechowywane w bazie SQLCipher, wyszukiwanie korzysta z FTS5, a klucz aktywnej sesji pozostaje w pamięci procesu GUI.

## Stan projektu

Aktualnie działa import Outlook/IMAP oraz pełna i przyrostowa synchronizacja Gmail API z CLI i GUI. Pipeline Gmail obejmuje trwałą kolejkę, pobieranie załączników, szyfrowany i deduplikowany magazyn blobów, ekstrakcję TXT/HTML/PDF/DOCX/XLSX/PPTX, lokalny OCR obrazów i mieszanych PDF-ów, transkrypcję audio/wideo przez FFmpeg i whisper.cpp oraz osobny indeks `content_fts`.

Dostępne jest opcjonalne wyszukiwanie semantyczne i hybrydowe przez lokalny endpoint. Dokładne wyszukiwanie FTS5 pozostaje podstawą projektu i nie jest zastępowane przez model. Bieżący status ustaleń technicznych znajduje się w [indeksie audytów](docs/audits/README.md).

## Wymagania

- Windows
- .NET 9 Desktop Runtime do uruchomienia; SDK 9.0.316 do budowania i testów
- źródło desktopowe obsługiwane przez integrację COM, konto IMAP albo konto Gmail połączone przez OAuth 2.0
- opcjonalny sprzętowy drugi składnik uwierzytelnienia
- opcjonalnie Tesseract 5 z danymi językowymi `pol` i `eng` do lokalnego OCR obrazów i skanowanych PDF-ów
- opcjonalnie Python, PaddlePaddle i PaddleOCR 3 do drugiej, całkowicie lokalnej próby OCR, gdy Tesseract nie zwróci tekstu
- opcjonalnie FFmpeg, `whisper-cli` z projektu whisper.cpp i lokalny model `ggml-small.bin` do transkrypcji audio/wideo
- opcjonalnie lokalny Ollama i model embeddingów do wyszukiwania semantycznego; aplikacja akceptuje wyłącznie endpoint loopback

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
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
```

## Gmail API i OAuth 2.0

Integracja Gmail korzysta wyłącznie z oficjalnego Gmail API i zakresu `gmail.readonly`. Aplikacja otwiera logowanie w systemowej przeglądarce, nie przyjmuje hasła do Gmaila i nie uruchamia własnego serwera poza tymczasowym odbiornikiem OAuth na lokalnym adresie loopback.

Zarządzanie kontami i synchronizacja Gmail są dostępne w CLI oraz w panelu otwieranym przyciskiem `Gmail` w GUI. Panel pokazuje konta, postęp synchronizacji, stan kolejki i umożliwia uruchomienie Workera. Pole wyszukiwania GUI pozwala wybrać wiadomości, zawartość załączników i transkrypcji, oba indeksy dokładne albo opcjonalny ranking hybrydowy.

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

Pierwszy import jest stronicowany i zapamiętuje checkpoint. Kolejne uruchomienia korzystają z historii zmian Gmaila; po wygaśnięciu `historyId` aplikacja automatycznie wykonuje kontrolowany full sync bez duplikowania danych. Wiadomości, których chwilowo nie udało się pobrać lub zapisać, trafiają do trwałej kolejki retry i są ponawiane przed kolejną porcją historii; pełna synchronizacja nie usuwa ich istniejących kopii z lokalnego korpusu. Importowane są także wiadomości zarchiwizowane, etykiety, flagi unread/spam/trash oraz metadane załączników.

## Załączniki, ekstrakcja i OCR

Worker pobiera załączniki Gmaila, IMAP i Outlooka do deduplikowanego magazynu szyfrowanego AES-GCM. IMAP zapisuje trwały locator obejmujący konto, folder, `UIDVALIDITY`, UID wiadomości, część MIME i kodowanie transferowe; późniejsze pobranie nie wymaga ponownego pobrania całej wiadomości. Outlook zapisuje `StoreID`, `EntryID` i indeks załącznika, a dostęp COM odbywa się na dedykowanym wątku STA. Gmail i IMAP przekazują jawną treść w pamięci. Ze względu na API Outlooka jego załącznik istnieje krótko w izolowanym katalogu roboczym; jest usuwany przed zakończeniem pobrania, a osierocone katalogi są sprzątane przy następnym zadaniu Outlook.

Obsługiwane ekstraktory deterministyczne obejmują TXT/CSV/XML/JSON, HTML, PDF z warstwą tekstową oraz DOCX/XLSX/PPTX. Segmenty zachowują odpowiednio numer strony, slajdu lub nazwę arkusza i trafiają do osobnego indeksu FTS5. Archiwa OpenXML podlegają limitom liczby wpisów, rozwiniętego rozmiaru i współczynnika kompresji; typy bez ekstraktora otrzymują status `skipped`.

OCR obrazów PNG/JPEG/TIFF/BMP oraz skanowanych stron PDF korzysta z lokalnego Tesseracta przez `stdin`/`stdout`, bez tworzenia jawnego pliku tymczasowego. PDF jest analizowany strona po stronie: zachowywany jest poprawny tekst istniejący, a przez PDFium renderowane są wyłącznie strony puste lub zawierające zbyt mało użytecznego tekstu. Obrazy PNG stron pozostają tylko w pamięci i są zerowane po OCR.

Opcjonalny fallback PaddleOCR jest domyślnie wyłączony. Worker uruchamia go tylko dla obrazu albo strony, dla której Tesseract zwrócił pusty wynik. Istniejący tekst Tesseracta nie jest porównywany, nadpisywany ani automatycznie „poprawiany”, dlatego drugi silnik nie może po cichu zmienić rozpoznanego numeru lub identyfikatora. Użycie fallbacku jest jawnie zapisane w `extractor_name` i `model_name`. Adapter `tools\paddleocr_runner.py` przekazuje obraz przez `stdin`, zwraca ograniczony JSON przez `stdout` i nie korzysta z usługi chmurowej.

Najprostsza instalacja wariantu CPU w osobnym, ignorowanym przez Git środowisku wygląda tak (zgodnie z [instrukcją PaddlePaddle](https://www.paddleocr.ai/main/en/version3.x/paddlepaddle_installation.html) i [instrukcją PaddleOCR](https://www.paddleocr.ai/main/en/version3.x/installation.html)):

```powershell
py -3.12 -m venv .tools\paddleocr
.tools\paddleocr\Scripts\python.exe -m pip install paddlepaddle==3.2.0 -i https://www.paddlepaddle.org.cn/packages/stable/cpu/
.tools\paddleocr\Scripts\python.exe -m pip install paddleocr
run\KKR.MailLens.exe config --paddleocr-enabled true --paddleocr-python "$PWD\.tools\paddleocr\Scripts\python.exe" --paddleocr-language pl --paddleocr-version PP-OCRv6 --paddleocr-device cpu --paddleocr-min-confidence 0.5 --paddleocr-timeout 300
```

Pierwsza inferencja może pobrać oficjalny model do lokalnego cache PaddleOCR; późniejsze rozpoznawanie odbywa się lokalnie. Ustawienie `--paddleocr-enabled false` wyłącza fallback bez wpływu na Tesseract, import, szyfrowanie, SQLite ani FTS5.

Domyślne języki to `pol+eng`, rozdzielczość PDF to 300 DPI, a limit jednego dokumentu wynosi 100 stron wymagających OCR. Ścieżkę, języki, timeouty i limity można ustawić poleceniem:

```powershell
run\KKR.MailLens.exe config --tesseract "C:\Program Files\Tesseract-OCR\tesseract.exe" --ocr-languages pol+eng --ocr-timeout 120 --ocr-pdf-dpi 300 --ocr-max-pdf-pages 100 --ocr-pdf-render-timeout 120 --ocr-pdf-batch-size 4 --worker-memory-mb 1536
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
run\KKR.MailLens.exe rebuild-content-index
```

PDF bez użytecznej warstwy tekstowej na co najmniej jednej stronie otrzymuje status `needs-ocr`, po czym Worker automatycznie zleca OCR tych stron. PDF jest otwierany raz na mały, konfigurowalny batch stron (domyślnie 4), a każdy bufor PNG jest zerowany natychmiast po OCR. Segmenty tekstowe i OCR są scalane według numeru strony i indeksowane w FTS5. Worker odnawia dzierżawę w tle przez cały czas pobierania, ekstrakcji, OCR, transkrypcji i tworzenia embeddingów; utrata dzierżawy anuluje operację i blokuje zapis jej wyniku.

`processing-run` uruchamia Workera z ograniczonym tokenem Windows, na nieinteraktywnym pulpicie i w Job Object z blokadą dostępu do schowka, ustawień interfejsu oraz globalnych uchwytów. Konfigurowalny łączny limit pamięci obejmuje także procesy potomne, w tym Tesseract i Python/PaddleOCR. Proces jest tworzony jako wstrzymany, ograniczenia są nakładane przed wykonaniem pierwszej instrukcji, a bezpośrednio uruchomiony `KKR.MailLens.Worker.exe` odmawia pracy. Ctrl+C i zablokowanie sesji anulują wszystkie aktywne operacje Workera; zadanie wraca do kolejki bez zużycia próby.

## Transkrypcja audio i wideo

Media są przekazywane do FFmpeg przez `stdin` i normalizowane do mono PCM 16 kHz. Whisper.cpp zapisuje lokalny JSON, którego segmenty wraz z `start_ms`/`end_ms` trafiają do `content_segments` oraz `content_fts`. Jawny WAV i JSON istnieją tylko w osobnym katalogu roboczym na czas zadania; katalog jest usuwany w `finally`, a osierocone katalogi po awarii są sprzątane przy następnym uruchomieniu transkrypcji.

Repozytorium nie zawiera binariów ani modelu. Zalecany model początkowy to wielojęzyczny `small`. Po zainstalowaniu lokalnych narzędzi ustaw ścieżki:

```powershell
run\KKR.MailLens.exe config --ffmpeg "C:\Tools\ffmpeg\bin\ffmpeg.exe" --whisper "C:\Tools\whisper.cpp\whisper-cli.exe" --whisper-model "C:\Models\ggml-small.bin" --whisper-fallback-model "C:\Models\ggml-medium.bin" --whisper-language auto --ffmpeg-timeout 600 --whisper-timeout 3600 --transcription-max-minutes 120
run\KKR.MailLens.exe processing-run
run\KKR.MailLens.exe query-content "neutralny tekst"
```

Transkrypcja jest całkowicie lokalna, bez diarization i usług sieciowych. Opcjonalny model fallback korzysta z tego samego lokalnego WAV i uruchamia się wyłącznie wtedy, gdy model podstawowy nie zwróci żadnego tekstu; nie zastępuje istniejącej transkrypcji. Pusta wartość `--whisper-fallback-model ""` wyłącza drugi przebieg. Limit pobieranego załącznika Gmail pozostaje bez zmian; domyślnie analizowane jest maksymalnie 120 minut jednego pliku.

## Lokalne embeddingi i wyszukiwanie hybrydowe

Wyszukiwanie semantyczne jest domyślnie wyłączone. Korzysta z lokalnego endpointu Ollama `/api/embed`; kod odrzuca adresy inne niż `localhost`, `127.0.0.1` lub inny adres IP loopback i wyłącza systemowy proxy dla tego połączenia. Należy wybrać model zainstalowany lokalnie, a nie model chmurowy udostępniany przez lokalny runtime. Wektory są przechowywane w bazie SQLCipher, powiązane z segmentami kluczem obcym i automatycznie usuwane razem z nimi. Tekst źródłowy, wynik OCR i transkrypcja nie są przez model poprawiane ani zastępowane.

Po lokalnym zainstalowaniu Ollama i pobraniu modelu embeddingów:

```powershell
ollama pull embeddinggemma
run\KKR.MailLens.exe config --semantic-enabled true --embedding-endpoint http://127.0.0.1:11434 --embedding-model embeddinggemma --embedding-batch-size 16 --semantic-max-candidates 25000
run\KKR.MailLens.exe semantic-index
run\KKR.MailLens.exe query-semantic "neutralny tekst"
```

`semantic-index --rebuild` odtwarza embeddingi wybranego modelu, a `query-semantic --semantic-only` pomija FTS5. Domyślne wyszukiwanie hybrydowe łączy ranking FTS5 i podobieństwo cosinusowe przez Reciprocal Rank Fusion, z większą wagą kanału dokładnego. Po włączeniu funkcji Worker automatycznie kolejkuje zadanie `embed` po zakończonej ekstrakcji, OCR lub transkrypcji. GUI udostępnia osobny zakres `Hybrydowe`.

## Konserwacja magazynu blobów

Zaszyfrowany blob może być współdzielony przez wiele wiadomości. Garbage collection usuwa plik dopiero po zniknięciu ostatniej aktywnej referencji i pomija dane używane przez działające zadanie Workera. Najpierw można wykonać bezpieczny podgląd:

```powershell
run\KKR.MailLens.exe blob-gc --dry-run
run\KKR.MailLens.exe blob-gc
```

Operacja usuwa również nieaktualne segmenty, wpisy FTS5 i zadania należące wyłącznie do usuniętych załączników. Przerwanie między usunięciem pliku i zapisem bazy jest bezpieczne — następne uruchomienie dokończy porządkowanie.

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

Tryb bez klucza sprzętowego przyjmuje wybrany przez użytkownika niepusty PIN; aplikacja nie udaje, że krótki PIN ma wysoką entropię. Dla publicznego lub wrażliwego korpusu należy użyć długiej frazy albo trybu `PIN + YubiKey`. Challenge YubiKey jest losową solą konkretnego korpusu; fizyczny dotyk jest wymagany tylko wtedy, gdy tak skonfigurowano slot urządzenia.

Historyczne raporty i aktualny status ich ustaleń są dostępne w [`docs/audits`](docs/audits/README.md).

## Polecenia

Pełną listę poleceń pokazuje:

```powershell
run\KKR.MailLens.exe help
```

Najważniejsze operacje to `init`, `status`, `lock`, `config`, `harvest`, `account`, `gmail`, `processing-run`, `processing-status`, `processing-retry`, `blob-gc`, `query`, `query-content`, `semantic-index`, `query-semantic`, `rebuild-content-index`, `stats`, `analyze`, `analyze-rules`, `reclassify` i `selftest`.

## Licencja

Kod źródłowy i dokumentacja KKR MailLens są udostępniane na warunkach [Apache License 2.0](LICENSE). Informacje wymagane przy redystrybucji znajdują się w pliku [NOTICE](NOTICE).

Nazwa „KKR MailLens”, logo i identyfikacja wizualna nie są udostępniane na warunkach licencji kodu; szczegóły opisuje [TRADEMARKS.md](TRADEMARKS.md). Komponenty zewnętrzne pozostają objęte własnymi licencjami.

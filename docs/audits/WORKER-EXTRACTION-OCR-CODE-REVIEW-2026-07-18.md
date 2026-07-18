# Code review — worker, ekstrakcja treści, wyszukiwanie i OCR

> Raport historyczny dla zakresu `e578ea0..d3391d9` (worker pobierania, ekstraktory
> PDF/OpenXML/tekst/HTML, dokumenty i segmenty treści, FTS5, OCR Tesseract, OCR stron PDF),
> zweryfikowany względem `main` na `03f5037`. Aktualny status ustaleń w [indeksie audytów](README.md).

**Data:** 2026-07-18
**Weryfikacja:** pełny zestaw testów przechodzi na HEAD (build przez SDK 9.0.1xx z `rollForward latestFeature`).

## Podsumowanie

Pipeline jest domknięty: synchronizacja Gmail enqueue'uje zadania `download`, osobny proces
`KKR.MailLens.Worker` (klucz sesji przez IPC z GUI, kontrola odblokowania przed każdym zadaniem)
wykonuje `download` → `extract` → `ocr`, wyniki trafiają do `content_documents`/`content_segments`
i indeksu FTS5 z triggerem sprzątającym po `DELETE`. OCR to lokalny Tesseract przez stdin/stdout
(bez plików tymczasowych z plaintextem), strony PDF renderowane do PNG w pamięci i zerowane po
użyciu. Jakość wysoka. Dwa ustalenia z pierwszego raportu zostały już naprawione w
`5c877a6` (fencing `Complete`/`Fail`) i `03f5037` (utwardzenie blob store).

## Istotne

### 1. Każda synchronizacja enqueue'uje `download` dla wszystkich widzianych załączników — także już pobranych

`MailAttachmentRepository.UpsertGmail` po commicie woła `Enqueue("download", id)` dla **każdego**
upsertowanego załącznika, bez sprawdzenia `download_status`. Ponowny upsert tej samej wiadomości
(pełna resynchronizacja, zmiana etykiet odebrana z historii Gmail) ponownie pobierze załącznik
z API (blob się zdeduplikuje, ale transfer idzie), a ścieżka download → `Enqueue("extract", ...)`
wywoła pełną re-ekstrakcję i re-indeksację dokumentu. Przy pełnym resyncu dużej skrzynki to
znaczący zbędny koszt. Symptom widać w testach: `ProcessingJobRepositoryTests.AddAttachment`
musiał przejść z zadań `download` na `extract`, bo upsert tworzy teraz niejawnie zadanie download.

**Poprawka:** enqueue tylko dla załączników z `download_status='metadata-only'` (osobny SELECT po
commicie albo warunkowe zbieranie idów).

### 2. Pusta strona skanu wywala cały dokument OCR

`OcrAttachmentProcessor.ExtractPdfAsync`: jeśli Tesseract zwróci pusty tekst dla strony, leci
`InvalidDataException` i całe zadanie pada; po `max_attempts` dokument ląduje w `failed` i nic
z niego nie trafia do indeksu. Puste/prawie puste strony są w skanach normalne, a
`PdfTextQuality.NeedsOcr` kieruje do OCR każdą stronę z <30 znakami alfanumerycznymi — strona
rozdzielająca w 50-stronicowym skanie trwale blokuje indeksację całości. To samo dla pojedynczego
obrazu bez tekstu (np. logo): `needs-ocr` → pusty OCR → wieczne `failed`.

**Poprawka:** pusty wynik OCR strony traktować jako pusty segment (pominąć stronę) i kontynuować;
dla obrazów bez tekstu zapisywać dokument jako `completed` z zerem segmentów.

## Średnie

### 3. Nieobsługiwane typy załączników lądują w `failed` jak błędy

`ContentExtractionDispatcher.Extract` rzuca `NotSupportedException` dla typów bez ekstraktora
(zip, exe, eml...), worker traktuje to jak każdy błąd → 3 próby → `failed` z
`error_code=NotSupportedException`. Statystyki błędów będą zaszumione permanentnymi „porażkami",
które są decyzją „nie indeksujemy tego typu". Warto dodać status `unsupported`/`skipped`.

### 4. Renderowanie PDF strona-po-stronie parsuje dokument od nowa przy każdej stronie

`ExtractPdfAsync` woła `renderer.RenderAsync(content, [pageNumber], ...)` w pętli, a
`PdfiumPageRenderer` przy każdym wywołaniu robi `GetPageCount` + pełne otwarcie dokumentu.
Dla skanu ze 100 stronami to 100 parsowań PDF-a. Zaleta obecnego kształtu: stała pamięć
(jedna strona naraz) i heartbeat między stronami — batche po kilka stron dałyby to samo taniej.

### 5. Worker nie ma anulowania — wszędzie `CancellationToken.None`

Zabicie workera w trakcie OCR zostawia zadanie `running` do wygaśnięcia lease (odzyskiwane
poprawnie), ale Tesseract/renderowanie nie dostaje sygnału i Ctrl+C nie działa czysto.
Podpięcie `Console.CancelKeyPress` → `CancellationTokenSource` byłoby tanie.

## Drobne

- **`TesseractOcrEngine`**: gdy proces padnie w trakcie pisania na stdin, `WriteAsync` rzuca
  `IOException` i ginie treść stderr (generyczny komunikat zamiast przyczyny z Tesseracta).
- **FTS**: metadane maila (temat/nadawca/odbiorcy) indeksowane osobno przy każdym segmencie —
  pompuje indeks i lekko wypacza bm25 dla wielosegmentowych dokumentów; świadomy trade-off.
- **`ExtractionResultBuilder`**: przy przycinaniu `raw` jest cięty budżetem znaków `clean`
  (długości się różnią) i może przeciąć parę zastępczą Unicode w połowie.
- **`HtmlContentExtractor`**: `raw_text` segmentu to pełny HTML (do 2 mln znaków) — celowe?
- **Restart pipeline'u**: crash między commitem `UpsertGmail` a `Enqueue` zostawia załącznik bez
  zadania na zawsze. Tani fix: okresowy sweep enqueue'ujący `metadata-only` bez aktywnego zadania.

## Braki pokrycia testowego

- Ścieżka `needs-ocr` → pusta odpowiedź Tesseracta (pkt 2) — brak testu.
- `RetryFailed` i `Counts` bez testów.
- Logika switch-a po `job_type` żyje w top-level `Program.cs` workera — nietestowalna; mogłaby
  mieszkać w Core.

## Co się podoba

- OCR przez stdin/stdout bez plików tymczasowych + zerowanie plaintextu/PNG po użyciu —
  konsekwentna higiena danych w pamięci.
- `RenewLease` z poprawnym fencingiem i heartbeat wydłużający lease w trakcie długiego OCR;
  po `5c877a6` ten sam wzorzec chroni `Complete`/`Fail`.
- Trigger `content_segments_after_delete` utrzymuje spójność FTS przy re-ekstrakcji; `Sanitize`
  opakowuje tokeny zapytania w cudzysłowy — brak wstrzykiwania składni FTS5.
- Wykrywanie typów po sygnaturze (nie po rozszerzeniu), z rozpoznaniem OpenXML po strukturze ZIP.
- `MarkDownloaded` czyści `inline_base64_data` po zmaterializowaniu blobu.
- Fałszywy Tesseract jako `.cmd` w testach + test kill-po-timeout.

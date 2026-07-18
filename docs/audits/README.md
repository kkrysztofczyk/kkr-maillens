# Audyty KKR MailLens

Raporty w tym katalogu są historycznymi migawkami kodu. Zachowujemy je jako źródło
ustaleń, ale przed zmianą zawsze weryfikujemy problem względem aktualnego `main`.

## Raporty

- [Audyt architektury i bezpieczeństwa](ARCHITECTURE-SECURITY-AUDIT-2026-07-18.md) — migawka `7f50d23`.
- [Code review pipeline załączników](ATTACHMENT-PIPELINE-CODE-REVIEW-2026-07-18.md) — zakres `f4f6b02..01351b8`.
- [Code review workera, ekstrakcji i OCR](WORKER-EXTRACTION-OCR-CODE-REVIEW-2026-07-18.md) — zakres `e578ea0..d3391d9`.

## Status bieżący

### Zamknięte lub nieaktualne

- SDK 9.0.316 jest zainstalowane i zgodne z `global.json`.
- Solution ma wspólny projekt Core, osobny Worker oraz brak ręcznie linkowanych plików C#.
- Migracje są wersjonowane; działają `PRAGMA foreign_keys=ON` i `busy_timeout`.
- Synchronizacja Gmail zapisuje wspólne `mail_attachments` przez UPSERT i zachowuje stan przetwarzania.
- Synchronizacja produkuje zadania `download`; Worker wykonuje `download`, `extract` i `ocr`.
- Aktywne zadania dokumentowe są deduplikowane indeksem z migracji 007.
- Działa szyfrowany i deduplikowany blob store, ekstrakcja dokumentów, segmenty oraz `content_fts`.
- Działa lokalny OCR obrazów i mieszanych PDF-ów strona po stronie.
- Dokumentacja opisuje Gmail, Workera, kolejkę, ekstrakcję i OCR.
- `Complete` i `Fail` stosują fencing po stanie `running` i identyfikatorze właściciela lease.
- Blob store zapisuje atomowo przy równoległej deduplikacji i używa AAD v2 obejmującego wersję formatu.
- Odczyt istniejących blobów v1 pozostaje zgodny wstecz, a KDF korzysta z systemowego `HKDF`.
- Ochrona przed path traversal blobów ma test regresyjny.
- Tokeny Gmail i hasła IMAP są związane z kluczem aktywnej sesji przez AES-GCM, a następnie chronione przez DPAPI `CurrentUser`.
- Starsze poświadczenia DPAPI-only są migrowane po odblokowaniu; ponowna inicjalizacja usuwa sekrety i bloby poprzedniego korpusu.
- Zapis strony Gmail obejmuje jedną transakcją wiadomości, korpus/FTS, metadane załączników i enqueue.
- Zadanie `download` powstaje wyłącznie dla załącznika w stanie `metadata-only`; ponowny upsert nie przetwarza gotowej treści.
- Walidacja rozmiaru Gmail nadal egzekwuje twardy limit pobrania, ale toleruje niewielką różnicę metadanych i odrzuca dopiero istotny rozjazd.
- Puste wyniki OCR strony lub obrazu kończą dokument poprawnie, zachowując pozostałe segmenty zamiast oznaczać całość jako `failed`.
- Opcjonalny lokalny fallback PaddleOCR działa tylko po pustym wyniku Tesseracta, używa ograniczonego protokołu `stdin`/JSON `stdout`, zapisuje pochodzenie wyniku i nie nadpisuje rozpoznanego tekstu pierwszego silnika.
- Strony PDF wymagające OCR są renderowane w konfigurowalnych batchach; kolejność jest walidowana, heartbeat działa po każdej stronie, a wszystkie bufory PNG są zerowane także przy błędzie.
- OpenXML ma limity liczby wpisów, rozmiaru po rozwinięciu i współczynnika kompresji oraz odrzuca niebezpieczne ścieżki archiwum.
- Nieobsługiwane typy kończą się statusem `skipped`, bez trzech bezcelowych prób i szumu w `failed`.
- Dekodowanie Base64URL ma jedną współdzieloną implementację, a inline base64 jest czyszczone po zapisaniu zaszyfrowanego blobu.
- Awaria pipe Tesseracta zachowuje ograniczony komunikat `stderr`; diagnostyka nie ginie pod ogólnym `IOException`.
- Limity tekstu nie przecinają par zastępczych Unicode, `raw_text` i `clean_text` mają osobne budżety, a ekstraktor HTML nie zapisuje całego źródłowego markup jako segmentu.
- Testy regresyjne obejmują przekroczenie limitu PDF i ekspansji archiwum OpenXML.
- Worker jest tworzony jako wstrzymany z ograniczonym tokenem Windows, po czym przed wznowieniem trafia do Job Object z limitami interfejsu i łącznym limitem pamięci obejmującym procesy potomne.
- Bezpośrednie uruchomienie Workera jest odrzucane, jeżeli proces nie ma ograniczonego tokenu.
- Ctrl+C i utrata odblokowanej sesji anulują operacje zewnętrzne; zadanie wraca do kolejki bez zużycia próby.
- Wszystkie typy zadań Workera mają niezależny heartbeat dzierżawy; utrata własności anuluje pracę, a ekstrakcja sprawdza ją ponownie przed zapisem wyniku.
- Odzyskanie wygasłego lease zachowuje wcześniejszy kod diagnostyczny, jeżeli zadanie już go miało.
- Działa lokalny pipeline FFmpeg → whisper.cpp z timestampami segmentów, FTS5 i sprzątaniem jawnych plików roboczych.
- Garbage collection usuwa wyłącznie bloby bez aktywnych referencji, zachowuje współdzielone bloby i pomija dane używane przez działające zadania.
- Usunięcie osieroconego blobu czyści nieaktualne dokumenty, segmenty, FTS i zadania usuniętych załączników; `blob-gc --dry-run` udostępnia podgląd.
- Test odbudowy `content_fts` potwierdza idempotentne odtworzenie indeksu z zapisanych segmentów.
- GUI ma panel kont Gmail z OAuth, synchronizacją pełną/przyrostową, anulowaniem, statusem kolejki i uruchamianiem Workera.
- Wyszukiwanie GUI przełącza się między wiadomościami, `content_fts` albo łączy oba rodzaje wyników.
- IMAP zapisuje locator konto/folder/UIDVALIDITY/UID/część MIME, a Worker pobiera i przetwarza załącznik przez MailKit z limitem pamięci.
- Outlook zapisuje StoreID/EntryID/indeks załącznika; broker COM działa na dedykowanym STA i sprząta izolowany plaintext workspace.
- Opcjonalne lokalne embeddingi są zapisywane w SQLCipher, automatycznie kolejkują się po przetworzeniu dokumentu i zasilają osobny ranking semantyczny lub hybrydowy FTS5 + RRF.
- Transkrypcja może użyć drugiego lokalnego modelu whisper.cpp wyłącznie po pustym wyniku modelu podstawowego; ponownie wykorzystuje ten sam WAV i zapisuje faktycznie użyty model.
- Endpoint embeddingów jest ograniczony do loopback i nie używa systemowego proxy; tekst OCR i transkrypcji nie jest modyfikowany przez model.
- Powtarzanie metadanych wiadomości przy segmentach FTS5 pozostaje świadomym kompromisem bieżącego schematu; daje prosty, odtwarzalny ranking kosztem większego indeksu.
- Polityka uwierzytelnienia pozostaje jawna: niepusty PIN jest dozwolony dla zgodności, a dokumentacja zaleca długą frazę lub `PIN + YubiKey`; gest dotyku zależy od konfiguracji slotu urządzenia.
- Zestaw testów wzrósł z historycznych 20/31 do 99 testów.

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
- Zestaw testów wzrósł z historycznych 20/31 do 61 testów.

### Otwarte — priorytet

- Utwardzić Worker przetwarzający niezaufane dokumenty: limity zasobów i uprawnień oraz testy złośliwych PDF/OOXML.
- Pusta strona skanu (pusty wynik Tesseracta) nie powinna wywalać całego dokumentu OCR do `failed` — pomijać stronę / zapisywać pusty segment.

### Otwarte — roadmapa

- Natychmiastowe anulowanie aktywnego zadania po zablokowaniu sesji.
- Status `unsupported`/`skipped` dla typów bez ekstraktora zamiast szumu w `failed`.
- Renderowanie stron PDF do OCR w batchach (dziś każdy `RenderAsync` parsuje dokument od nowa).
- Graceful shutdown workera (`Console.CancelKeyPress` → `CancellationTokenSource`).
- Garbage collection zaszyfrowanych blobów bez referencji.
- Testy odbudowy FTS i współdzielonych blobów.
- Obsługa Gmaila i wyników `content_fts` w GUI.
- Załączniki Outlook/IMAP, transkrypcja audio/wideo oraz opcjonalne lokalne AI.

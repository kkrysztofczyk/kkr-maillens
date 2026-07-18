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
- Puste wyniki OCR strony lub obrazu kończą dokument poprawnie, zachowując pozostałe segmenty zamiast oznaczać całość jako `failed`.
- OpenXML ma limity liczby wpisów, rozmiaru po rozwinięciu i współczynnika kompresji oraz odrzuca niebezpieczne ścieżki archiwum.
- Nieobsługiwane typy kończą się statusem `skipped`, bez trzech bezcelowych prób i szumu w `failed`.
- Testy regresyjne obejmują przekroczenie limitu PDF i ekspansji archiwum OpenXML.
- Worker uruchamiany przez CLI działa w Windows Job Object z łącznym limitem pamięci obejmującym procesy potomne.
- Ctrl+C i utrata odblokowanej sesji anulują operacje zewnętrzne; zadanie wraca do kolejki bez zużycia próby.
- Działa lokalny pipeline FFmpeg → whisper.cpp z timestampami segmentów, FTS5 i sprzątaniem jawnych plików roboczych.
- Garbage collection usuwa wyłącznie bloby bez aktywnych referencji, zachowuje współdzielone bloby i pomija dane używane przez działające zadania.
- Usunięcie osieroconego blobu czyści nieaktualne dokumenty, segmenty, FTS i zadania usuniętych załączników; `blob-gc --dry-run` udostępnia podgląd.
- Test odbudowy `content_fts` potwierdza idempotentne odtworzenie indeksu z zapisanych segmentów.
- GUI ma panel kont Gmail z OAuth, synchronizacją pełną/przyrostową, anulowaniem, statusem kolejki i uruchamianiem Workera.
- Wyszukiwanie GUI przełącza się między wiadomościami, `content_fts` albo łączy oba rodzaje wyników.
- IMAP zapisuje locator konto/folder/UIDVALIDITY/UID/część MIME, a Worker pobiera i przetwarza załącznik przez MailKit z limitem pamięci.
- Zestaw testów wzrósł z historycznych 20/31 do 78 testów.

### Otwarte — priorytet

- Rozważyć dalszą izolację uprawnień Workera (osobny ograniczony token/profil); obecnie izolacja obejmuje osobny proces, limit pamięci, limity dokumentów i anulowanie.

### Otwarte — roadmapa

- Renderowanie stron PDF do OCR w batchach (dziś każdy `RenderAsync` parsuje dokument od nowa).
- Załączniki Outlook oraz opcjonalne lokalne AI.

# Audyty KKR MailLens

Raporty w tym katalogu są historycznymi migawkami kodu. Zachowujemy je jako źródło
ustaleń, ale przed zmianą zawsze weryfikujemy problem względem aktualnego `main`.

## Raporty

- [Audyt architektury i bezpieczeństwa](ARCHITECTURE-SECURITY-AUDIT-2026-07-18.md) — migawka `7f50d23`.
- [Code review pipeline załączników](ATTACHMENT-PIPELINE-CODE-REVIEW-2026-07-18.md) — zakres `f4f6b02..01351b8`.

## Status na `d3391d9`

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
- Zestaw testów wzrósł z historycznych 20/31 do 50 testów.

### Otwarte — priorytet

- Powiązać ochronę tokenów Gmail i haseł IMAP z odblokowaną sesją; obecnie podstawową ochroną jest DPAPI `CurrentUser`.
- Dodać fencing do `ProcessingJobRepository.Complete` i `Fail`: aktualizować wyłącznie zadanie `running` należące do danego workera.
- Usunąć wyścig `File.Exists`/`File.Move` podczas równoległego zapisu identycznego blobu.
- Uwierzytelniać w AAD również wersję formatu blobu, nie tylko magic header.
- Rozważyć jedną transakcję dla zapisu wiadomości, korpusu, metadanych załączników i enqueue strony synchronizacji.
- Utwardzić Worker przetwarzający niezaufane dokumenty: limity zasobów i uprawnień oraz testy złośliwych PDF/OOXML.

### Otwarte — roadmapa

- Natychmiastowe anulowanie aktywnego zadania po zablokowaniu sesji.
- Garbage collection zaszyfrowanych blobów bez referencji.
- Testy fencing kolejki, path traversal, odbudowy FTS i współdzielonych blobów.
- Obsługa Gmaila i wyników `content_fts` w GUI.
- Załączniki Outlook/IMAP, transkrypcja audio/wideo oraz opcjonalne lokalne AI.

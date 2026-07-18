namespace KKR.MailLens;

/// <summary>Komendy CLI. Sesje trzyma GUI (klucz w RAM); CLI bierze klucz z agenta przez named-pipe,
/// sam nie odblokowuje. `status` pokazuje stan sesji, `lock` ja czysci.</summary>
static class Cli
{
    public static int Init(string[] args)
    {
        bool force = Flag(args, "--force");
        bool wantYubi = Flag(args, "--yubi");
        // PIN tylko z --pin albo interaktywnie/stdin. NIE z env (sekret wyciekalby do potomkow/crash-dumpow).
        string? pin = GetStr(args, "--pin");
        if (string.IsNullOrEmpty(pin))
        {
            if (Console.IsInputRedirected) pin = Console.ReadLine();
            else
            {
                pin = PromptMasked("Ustaw PIN: ");
                if (PromptMasked("Powtorz PIN: ") != pin) { Console.Error.WriteLine("PIN sie nie zgadza."); return 1; }
            }
        }
        if (string.IsNullOrEmpty(pin)) { Console.Error.WriteLine("Pusty PIN."); return 1; }

        if (wantYubi && YubiKey.TryInfo(out var info)) Console.WriteLine($"YubiKey: {info} - licze odpowiedz challenge-response...");
        var r = Setup.Init(pin!, wantYubi, force);
        if (r.Error != null) { Console.Error.WriteLine(r.Error); return 1; }

        Console.WriteLine($"Zainicjowano zaszyfrowany korpus [{(wantYubi ? "PIN + YubiKey" : "PIN")}]. Teraz odblokuj w aplikacji GUI (ona trzyma sesje w RAM), potem 'harvest'.");
        return 0;
    }

    // W modelu RAM sesje trzyma agent (GUI). CLI z niej korzysta, ale nie unlockuje samodzielnie.
    public static int Unlock(string[] args)
    {
        Console.WriteLine("Sesje trzyma aplikacja GUI (klucz w RAM). Odblokuj tam - CLI skorzysta automatycznie.");
        PrintStatus();
        return 0;
    }

    public static int Status() { PrintStatus(); return 0; }

    /// <summary>Pokaz/ustaw konfiguracje harvestu (config.json): filtr skrzynki + limit/folder.</summary>
    public static int Config(string[] args)
    {
        var cfg = AppConfig.Load();
        bool changed = false;
        if (GetStr(args, "--store") is { } s) { cfg.StoreFilter = s.Trim(); changed = true; }
        if (GetStr(args, "--max") is { } mx && int.TryParse(mx, out var m)) { cfg.MaxPerFolder = m; changed = true; }
        if (GetStr(args, "--tesseract") is { } tess) { cfg.TesseractPath = tess.Trim(); changed = true; }
        if (GetStr(args, "--ocr-languages") is { } languages) { cfg.OcrLanguages = languages.Trim(); changed = true; }
        if (GetStr(args, "--ocr-timeout") is { } timeout && int.TryParse(timeout, out int seconds))
        { cfg.OcrTimeoutSeconds = Math.Clamp(seconds, 10, 3600); changed = true; }
        if (GetStr(args, "--ocr-pdf-dpi") is { } dpi && int.TryParse(dpi, out int dpiValue))
        { cfg.OcrPdfDpi = Math.Clamp(dpiValue, 72, 600); changed = true; }
        if (GetStr(args, "--ocr-max-pdf-pages") is { } pages && int.TryParse(pages, out int pageCount))
        { cfg.OcrMaxPdfPages = Math.Clamp(pageCount, 1, 10_000); changed = true; }
        if (GetStr(args, "--ocr-pdf-render-timeout") is { } renderTimeout
            && int.TryParse(renderTimeout, out int renderSeconds))
        { cfg.OcrPdfRenderTimeoutSeconds = Math.Clamp(renderSeconds, 10, 3600); changed = true; }
        if (GetStr(args, "--worker-memory-mb") is { } workerMemory
            && int.TryParse(workerMemory, out int memoryMb))
        { cfg.WorkerMemoryLimitMb = Math.Clamp(memoryMb, 256, 16_384); changed = true; }
        if (GetStr(args, "--ffmpeg") is { } ffmpeg) { cfg.FfmpegPath = ffmpeg.Trim(); changed = true; }
        if (GetStr(args, "--whisper") is { } whisper) { cfg.WhisperPath = whisper.Trim(); changed = true; }
        if (GetStr(args, "--whisper-model") is { } model) { cfg.WhisperModelPath = model.Trim(); changed = true; }
        if (GetStr(args, "--whisper-language") is { } language) { cfg.WhisperLanguage = language.Trim(); changed = true; }
        if (GetStr(args, "--ffmpeg-timeout") is { } ffmpegTimeout && int.TryParse(ffmpegTimeout, out int ffmpegSeconds))
        { cfg.FfmpegTimeoutSeconds = Math.Clamp(ffmpegSeconds, 10, 3600); changed = true; }
        if (GetStr(args, "--whisper-timeout") is { } whisperTimeout && int.TryParse(whisperTimeout, out int whisperSeconds))
        { cfg.WhisperTimeoutSeconds = Math.Clamp(whisperSeconds, 30, 24 * 3600); changed = true; }
        if (GetStr(args, "--transcription-max-minutes") is { } duration && int.TryParse(duration, out int minutes))
        { cfg.TranscriptionMaxMinutes = Math.Clamp(minutes, 1, 24 * 60); changed = true; }
        if (changed) { cfg.Save(); Console.WriteLine($"Zapisano config: {Paths.ConfigFile}"); }

        Console.WriteLine($"Katalog danych: {Paths.Base}");
        Console.WriteLine($"store filter : '{cfg.StoreFilter}'{(cfg.StoreFilter.Length == 0 ? "  (wszystkie skrzynki)" : "")}");
        Console.WriteLine($"max/folder   : {cfg.MaxPerFolder}{(cfg.MaxPerFolder <= 0 ? "  (bez limitu)" : "")}");
        Console.WriteLine($"Tesseract    : {cfg.TesseractPath}");
        Console.WriteLine($"OCR          : {cfg.OcrLanguages}, timeout {cfg.OcrTimeoutSeconds} s");
        Console.WriteLine($"OCR PDF      : {cfg.OcrPdfDpi} DPI, max {cfg.OcrMaxPdfPages} stron, render timeout {cfg.OcrPdfRenderTimeoutSeconds} s/strona");
        Console.WriteLine($"Worker       : limit pamięci {cfg.WorkerMemoryLimitMb} MiB");
        Console.WriteLine($"FFmpeg       : {cfg.FfmpegPath}, timeout {cfg.FfmpegTimeoutSeconds} s");
        Console.WriteLine($"whisper.cpp  : {cfg.WhisperPath}, model '{cfg.WhisperModelPath}', język {cfg.WhisperLanguage}, timeout {cfg.WhisperTimeoutSeconds} s");
        Console.WriteLine($"Transkrypcja : max {cfg.TranscriptionMaxMinutes} min");
        if (!changed) Console.WriteLine("Zmiana: config [--store <fragment>] [--max N] [--tesseract <sciezka>] [--ocr-languages pol+eng] [--ocr-timeout N] [--ocr-pdf-dpi N] [--ocr-max-pdf-pages N] [--ocr-pdf-render-timeout N] [--worker-memory-mb N] [--ffmpeg <sciezka>] [--whisper <sciezka>] [--whisper-model <plik>] [--whisper-language auto] [--ffmpeg-timeout N] [--whisper-timeout N] [--transcription-max-minutes N]");
        return 0;
    }

    public static int Harvest(string[] args)
    {
        string? keyHex = RequireKey();
        if (keyHex is null) return 2;

        var cfg = AppConfig.Load(); // domyslny store/limit z config.json (nie zaszyte w kodzie)
        string store = GetStr(args, "--store") ?? cfg.StoreFilter;
        DateTime? since = ParseDate(GetStr(args, "--since"));
        int maxPerFolder = GetInt(args, "--max", cfg.MaxPerFolder);
        if (maxPerFolder <= 0) maxPerFolder = 1_000_000; // <=0 = bez limitu
        string? foldersArg = GetStr(args, "--folders");
        string[]? includeLeaves = string.IsNullOrWhiteSpace(foldersArg)
            ? null
            : foldersArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Console.WriteLine($"Harvest: store~'{(store.Length == 0 ? "(wszystkie skrzynki)" : store)}', foldery={(includeLeaves is null ? "wszystkie (bez Deleted/Drafts/Junk/Outbox)" : string.Join("|", includeLeaves))}, since={(since?.ToString("yyyy-MM-dd") ?? "(brak)")}, max/folder={maxPerFolder}");

        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var c = Db.Open(keyHex, create: false); // init tworzy baze; harvest jej nie tworzy po cichu
        Db.EnsureSchema(c);
        int ins = 0, upd = 0;

        try
        {
            using var ol = new Outlook();
            int totalMails = ol.HarvestMail(store, since, maxPerFolder,
                f => Console.WriteLine($"  folder: {f}"),
                (done, total) => Console.WriteLine($"    ...{(total > 0 ? done * 100 / total : 0)}% ({done}/{total})"),
                batch => { var st = Corpus.Upsert(c, batch, stamp); ins += st.Inserted; upd += st.Updated; }, // zapis porcjami
                includeLeaves);
            Console.WriteLine($"Zebrano z Outlooka: {totalMails} maili.");
        }
        catch (Exception ex) { Console.Error.WriteLine($"Blad Outlook COM: {ex.Message} (juz zapisane porcje zostaly w korpusie)"); return 1; }

        Console.WriteLine($"Gotowe: +{ins} nowych, {upd} zaktualizowanych. Korpus lacznie: {Corpus.Count(c)} maili.");
        return 0;
    }

    public static int Query(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw 'harvest'."); return 1; }
        return KKR.MailLens.Query.Run(key, args);
    }

    public static int QueryContent(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw uruchom import."); return 1; }
        return ContentSearch.Run(key, args);
    }

    public static int RebuildContentIndex()
    {
        string? key = RequireKey(); if (key is null) return 2;
        using var connection = Db.Open(key, create: false);
        Db.EnsureSchema(connection);
        Console.WriteLine($"Odbudowano indeks segmentów: {ContentSearch.Rebuild(connection)}");
        return 0;
    }

    public static int Stats()
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw 'harvest'."); return 1; }
        return KKR.MailLens.Query.Stats(key);
    }

    public static int Analyze(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw 'harvest'."); return 1; }
        return KKR.MailLens.Query.Analyze(key, GetInt(args, "--top", 20));
    }

    public static int AnalyzeRules()
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw 'harvest'."); return 1; }
        return KKR.MailLens.Query.AnalyzeRules(key);
    }

    public static int Reclassify()
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus pusty - najpierw 'harvest'."); return 1; }
        using var c = Db.Open(key, create: false);
        Db.EnsureSchema(c);
        int alerts = Corpus.Reclassify(c);
        long total = Corpus.Count(c);
        Console.WriteLine($"Reclassify: 'alert'={alerts}, 'mail'={total - alerts} (z {total}). Domyslne 'query' pomija alerty.");
        return 0;
    }

    public static int YubiTest()
    {
        if (!YubiKey.TryInfo(out var info)) { Console.Error.WriteLine($"YubiKey: {info}"); return 1; }
        Console.WriteLine($"YubiKey: {info}");
        byte[] challenge = Convert.FromHexString("00112233445566778899aabbccddeeff00112233");

        Console.WriteLine("Challenge-response (SDK). Gdy zobaczysz DOTKNIJ - dotknij zlotego pola:");
        var r1 = YubiKey.ChallengeResponse(challenge, () => Console.WriteLine("  >>> DOTKNIJ KLUCZA TERAZ <<<"));
        string hex = Convert.ToHexString(r1).ToLowerInvariant();
        Console.WriteLine("  odpowiedz: " + hex);
        bool matchesYkman = hex == "54c594dc8ab34e99584a45960670754b089e0cf6";
        Console.WriteLine(matchesYkman
            ? ">>> OK: SDK dziala + odpowiedz zgodna z ykman (HMAC-SHA1 deterministyczny) <<<"
            : ">>> SDK dziala (odpowiedz jak wyzej; sprawdz czy stabilna miedzy wywolaniami) <<<");
        return 0;
    }

    // Klucz z agenta (GUI trzyma go w RAM). Bez dzialajacego/odblokowanego GUI = brak sesji.
    // ---- IMAP (zrodlo niezalezne od Outlooka) ----

    public static int ImapAdd(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        var accts = ImapAccounts.Load();
        var a = new ImapAccount();
        a.Host = GetStr(args, "--host") ?? a.Host;
        a.Port = GetInt(args, "--port", a.Port == 0 ? 993 : a.Port);
        a.User = GetStr(args, "--user") ?? "";
        if (Flag(args, "--starttls")) a.UseSsl = false;
        a.Name = GetStr(args, "--name") ?? a.User;
        if (string.IsNullOrEmpty(a.Host) || string.IsNullOrEmpty(a.User))
        { Console.Error.WriteLine("Podaj --host i --user."); return 1; }

        // Haslo tylko z --pass albo interaktywnie (nie z env - patrz uwaga o PIN wyzej).
        string? pass = GetStr(args, "--pass");
        if (string.IsNullOrEmpty(pass)) pass = PromptMasked($"Haslo / App-Password dla {a.User}: ");
        if (string.IsNullOrEmpty(pass)) { Console.Error.WriteLine("Brak hasla."); return 1; }
        a.SetPassword(pass, key);

        accts.Accounts.RemoveAll(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
        accts.Accounts.Add(a);
        accts.Save();
        Console.WriteLine($"Zapisano konto IMAP '{a.Name}' ({a.User}@{a.Host}:{a.Port}, ssl={a.UseSsl}). Hasło chronione aktywną sesją i DPAPI.");
        return 0;
    }

    public static int ImapList()
    {
        var accts = ImapAccounts.Load();
        if (accts.Accounts.Count == 0) { Console.WriteLine("Brak kont IMAP. Dodaj: imap-add --host <host> --user sender@example.invalid"); return 0; }
        Console.WriteLine("Konta IMAP:");
        foreach (var a in accts.Accounts)
            Console.WriteLine($"  {a.Name,-20} {a.User}@{a.Host}:{a.Port} ssl={a.UseSsl}");
        return 0;
    }

    public static int ImapHarvest(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        var accts = ImapAccounts.Load();
        try { accts.MigrateCredentials(key); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
            or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine("Nie można odszyfrować hasła IMAP dla aktywnej sesji.");
            return 1;
        }
        string? name = GetStr(args, "--account");
        var chosen = new List<ImapAccount>();
        if (name != null) { var a = accts.Find(name); if (a != null) chosen.Add(a); }
        else chosen.AddRange(accts.Accounts);
        if (chosen.Count == 0) { Console.Error.WriteLine("Brak kont IMAP (imap-add) albo zle --account."); return 1; }

        DateTime? since = ParseDate(GetStr(args, "--since"));
        int max = GetInt(args, "--max", 5000);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        using var c = Db.Open(key, create: true);
        Db.EnsureSchema(c);
        int ins = 0, upd = 0;

        foreach (var a in chosen)
        {
            Console.WriteLine($"IMAP '{a.Name}' ({a.User}@{a.Host}): since={(since?.ToString("yyyy-MM-dd") ?? "(brak)")}, max/folder={max}");
            try
            {
                int n = Imap.Harvest(a, key, since, max,
                    f => Console.WriteLine($"  folder: {f}"),
                    (done, total) => Console.WriteLine($"    ...{(total > 0 ? done * 100 / total : 0)}% ({done}/{total})"),
                    batch => { var st = Corpus.Upsert(c, batch, stamp); ins += st.Inserted; upd += st.Updated; });
                Console.WriteLine($"  zebrano {n} z '{a.Name}'.");
            }
            catch (Exception ex) { Console.Error.WriteLine($"  Blad IMAP '{a.Name}': {ex.Message} (zapisane porcje zostaly)"); }
        }
        Console.WriteLine($"Gotowe: +{ins} nowych, {upd} zaktualizowanych. Korpus lacznie: {Corpus.Count(c)} maili.");
        return 0;
    }

    // ---- Gmail API (OAuth 2.0, bez hasla do skrzynki) ----

    public static int Account(string[] args)
    {
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        return sub switch
        {
            "add" when args.Length > 2 && args[2].Equals("gmail", StringComparison.OrdinalIgnoreCase) => AccountAddGmail(),
            "list" => AccountList(),
            "remove" when args.Length > 2 => AccountRemove(args[2]),
            _ => AccountHelp(),
        };
    }

    public static int Gmail(string[] args)
    {
        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
        return sub switch
        {
            "sync" => GmailSync(args),
            "status" => GmailStatus(args),
            "cancel" => GmailCancel(),
            _ => GmailHelp(),
        };
    }

    static int AccountAddGmail()
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus nie istnieje - najpierw 'init'."); return 1; }
        return RunCancelable(async cancellationToken =>
        {
            using var c = Db.Open(key, create: false);
            Db.EnsureSchema(c);
            Console.WriteLine("Otwieram systemowa przegladarke do logowania OAuth. Haslo do Gmaila nie trafia do aplikacji.");
            GmailAccountRecord account = await GmailOAuth.ConnectAsync(c, key, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Połączono konto: {account.Email}. Refresh token chroni aktywna sesja i DPAPI.");
            return 0;
        });
    }

    static int AccountList()
    {
        string? key = RequireKey(); if (key is null) return 2;
        if (!File.Exists(Paths.CorpusDb)) { Console.WriteLine("Brak polaczonych kont."); return 0; }
        using var c = Db.Open(key, create: false);
        Db.EnsureSchema(c);
        var accounts = GmailRepository.ListAccounts(c);
        if (accounts.Count == 0) { Console.WriteLine("Brak polaczonych kont Gmail."); return 0; }
        Console.WriteLine("Konta:");
        foreach (var account in accounts)
            Console.WriteLine($"  {account.Id,4}  {account.Email}  provider=gmail");
        return 0;
    }

    static int AccountRemove(string selector)
    {
        string? key = RequireKey(); if (key is null) return 2;
        using var c = Db.Open(key, create: false);
        Db.EnsureSchema(c);
        GmailAccountRecord? account = GmailRepository.FindAccount(c, selector);
        if (account is null) { Console.Error.WriteLine("Nie znaleziono konta."); return 1; }
        GmailOAuth.RemoveTokenAsync(account.TokenKey, key).GetAwaiter().GetResult();
        GmailRepository.DeleteAccount(c, account.Id);
        Console.WriteLine($"Odlaczono konto {account.Email}; lokalny token i dane synchronizacji zostaly usuniete.");
        return 0;
    }

    static int GmailSync(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        bool full = Flag(args, "--full");
        string? selector = GetStr(args, "--account");
        if (!File.Exists(Paths.CorpusDb)) { Console.Error.WriteLine("Korpus nie istnieje - najpierw 'init'."); return 1; }

        return RunCancelable(async cancellationToken =>
        {
            GmailCancellation.Clear();
            try
            {
                using var c = Db.Open(key, create: false);
                Db.EnsureSchema(c);
                IReadOnlyList<GmailAccountRecord> accounts = selector is null
                    ? GmailRepository.ListAccounts(c)
                    : GmailRepository.FindAccount(c, selector) is { } selected ? [selected] : Array.Empty<GmailAccountRecord>();
                if (accounts.Count == 0) { Console.Error.WriteLine("Brak pasujacego konta. Uzyj 'account add gmail'."); return 1; }

                int exit = 0;
                foreach (GmailAccountRecord account in accounts)
                {
                    GmailCancellation.ThrowIfRequested(cancellationToken);
                    Console.WriteLine($"Gmail sync: {account.Email} ({(full ? "pelna" : "automatyczna")}).");
                    try
                    {
                        using IGmailApiClient api = await GmailOAuth.CreateApiClientAsync(account, key, cancellationToken).ConfigureAwait(false);
                        var progress = new InlineProgress<GmailSyncProgress>(p =>
                            Console.WriteLine($"  {p.Phase}: przetworzono={p.Processed}, bledy={p.Errors}"));
                        var synchronizer = new GmailSynchronizer(c, api, progress);
                        GmailSyncResult result = await synchronizer.SyncAsync(account, full, cancellationToken).ConfigureAwait(false);
                        Console.WriteLine($"  gotowe: przetworzono={result.Processed}, +{result.Inserted}, aktualizacje={result.Updated}, usuniete={result.Deleted}, bledy={result.Errors}");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Synchronizacja nieudana: {SafeError(ex)}");
                        exit = 1;
                    }
                }
                return exit;
            }
            finally { GmailCancellation.Clear(); }
        });
    }

    static int GmailStatus(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        string? selector = GetStr(args, "--account");
        using var c = Db.Open(key, create: false);
        Db.EnsureSchema(c);
        IReadOnlyList<GmailAccountRecord> accounts = selector is null
            ? GmailRepository.ListAccounts(c)
            : GmailRepository.FindAccount(c, selector) is { } selected ? [selected] : Array.Empty<GmailAccountRecord>();
        if (accounts.Count == 0) { Console.WriteLine("Brak polaczonych kont Gmail."); return 0; }
        foreach (var account in accounts)
        {
            Console.WriteLine($"Konto: {account.Email}");
            Console.WriteLine($"  wiadomosci       : {GmailRepository.MessageCount(c, account.Id)}");
            Console.WriteLine($"  ostatni sync     : {account.LastSyncAt ?? "(brak)"}");
            Console.WriteLine($"  pierwszy import  : {(account.InitialSyncCompleted ? "zakonczony" : "w toku / niewykonany")}");
            Console.WriteLine($"  bledy lacznie    : {GmailRepository.ErrorCount(c, account.Id)}");
            Console.WriteLine($"  ostatnie bledy   : {account.LastErrorCount}");
            Console.WriteLine($"  operacja         : {account.CurrentOperation ?? "bezczynna"}");
        }
        return 0;
    }

    static int GmailCancel()
    {
        GmailCancellation.Request();
        Console.WriteLine("Zgloszono anulowanie synchronizacji Gmail.");
        return 0;
    }

    public static int ProcessingStatus()
    {
        string? key = RequireKey(); if (key is null) return 2;
        using var connection = Db.Open(key, create: false);
        Db.EnsureSchema(connection);
        IReadOnlyDictionary<string, long> counts = ProcessingJobRepository.Counts(connection);
        foreach (string status in new[] { "pending", "running", "completed", "failed" })
            Console.WriteLine($"{status,-10}: {counts.GetValueOrDefault(status)}");
        return 0;
    }

    public static int ProcessingRetry()
    {
        string? key = RequireKey(); if (key is null) return 2;
        using var connection = Db.Open(key, create: false);
        Db.EnsureSchema(connection);
        Console.WriteLine($"Przywrócono do kolejki: {ProcessingJobRepository.RetryFailed(connection)}");
        return 0;
    }

    public static int BlobGc(string[] args)
    {
        string? key = RequireKey(); if (key is null) return 2;
        using var connection = Db.Open(key, create: false);
        Db.EnsureSchema(connection);
        if (Flag(args, "--dry-run"))
        {
            BlobGarbageCollectionResult preview = BlobGarbageCollector.Preview(connection);
            Console.WriteLine($"Osierocone bloby: {preview.Orphaned}; możliwe do odzyskania: {FormatBytes(preview.ReclaimedBytes)}.");
            return 0;
        }

        BlobGarbageCollectionResult result = BlobGarbageCollector.Collect(connection, Paths.BlobsDir,
            message => Console.Error.WriteLine(message));
        Console.WriteLine($"Usunięto blobów: {result.Deleted}/{result.Orphaned}; odzyskano: {FormatBytes(result.ReclaimedBytes)}; błędy: {result.Failed}.");
        return result.Failed == 0 ? 0 : 1;
    }

    public static int ProcessingRun(string[] args)
    {
        if (RequireKey() is null) return 2;
        string executable = Path.Combine(AppContext.BaseDirectory, "KKR.MailLens.Worker.exe");
        if (!File.Exists(executable))
        {
            Console.Error.WriteLine($"Brak workera: {executable}. Opublikuj projekt KKR.MailLens.Worker do tego samego katalogu.");
            return 1;
        }
        var start = new System.Diagnostics.ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            Arguments = Flag(args, "--once") ? "" : "--drain",
        };
        using var process = System.Diagnostics.Process.Start(start);
        if (process is null) return 1;
        int memoryLimitMb = Math.Clamp(AppConfig.Load().WorkerMemoryLimitMb, 256, 16_384);
        using WorkerProcessLimit? processLimit = WorkerProcessLimit.TryAttach(process,
            memoryLimitMb * 1024L * 1024L, out string? limitError);
        if (processLimit is null && limitError is not null)
            Console.Error.WriteLine($"UWAGA: nie ustawiono limitu pamięci Workera: {limitError}");
        process.WaitForExit();
        return process.ExitCode;
    }

    static int AccountHelp()
    {
        Console.WriteLine("Uzycie: account add gmail | account list | account remove <id|adres>");
        return 1;
    }

    static int GmailHelp()
    {
        Console.WriteLine("Uzycie: gmail sync [--account <id|adres>] [--full] | gmail status [--account <id|adres>] | gmail cancel");
        return 1;
    }

    static int RunCancelable(Func<CancellationToken, Task<int>> action)
    {
        using var source = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; source.Cancel(); GmailCancellation.Request(); };
        Console.CancelKeyPress += handler;
        try { return action(source.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operacja anulowana."); return 130; }
        catch (Exception ex) { Console.Error.WriteLine(SafeError(ex)); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }

    static string SafeError(Exception exception) => exception switch
    {
        GmailAuthorizationException => exception.Message,
        FileNotFoundException => exception.Message,
        InvalidOperationException => exception.Message,
        _ => exception.GetType().Name,
    };

    sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    static string? RequireKey()
    {
        var k = Ipc.Request("GETKEY");
        if (string.IsNullOrEmpty(k) || k == "LOCKED")
        {
            Console.Error.WriteLine(k is null
                ? "Brak agenta - uruchom aplikacje GUI i odblokuj (ona trzyma klucz w RAM)."
                : "Zablokowane - odblokuj w aplikacji GUI.");
            return null;
        }
        return k;
    }

    static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null;

    public static int Lock()
    {
        var resp = Ipc.Request("LOCK");
        Console.WriteLine(resp == "OK" ? "Zablokowano sesje GUI (klucz usuniety z RAM)." : "Brak agenta GUI - nic do zablokowania.");
        return 0;
    }

    public static int Help()
    {
        Console.WriteLine("""
            KKR MailLens - zaszyfrowany lokalny korpus poczty.

            Sesje trzyma aplikacja GUI (klucz w RAM). CLI korzysta z niej przez lokalny agent (named pipe).

            Komendy:
              init [--pin <PIN>] [--yubi] [--force]
                                                   USTAW PIN (pyta 2x) + zwiaz YubiKey i UTWORZ zaszyfrowana baze.
                                                   Krok pierwszy. --force nadpisuje (KASUJE dane). Potem odblokuj w GUI.
              status                               stan sesji (z agenta GUI) + katalog danych
              lock                                 zablokuj sesje GUI (usun klucz z RAM)
              config [--store <fragm>] [--max <N>] [--tesseract <sciezka>] [--ocr-languages pol+eng]
                     [--ocr-timeout N] [--ocr-pdf-dpi N] [--ocr-max-pdf-pages N]
                     [--ocr-pdf-render-timeout N] [--worker-memory-mb N]
                     [--ffmpeg <sciezka>] [--whisper <sciezka>] [--whisper-model <plik>]
                     [--whisper-language auto] [--ffmpeg-timeout N] [--whisper-timeout N]
                     [--transcription-max-minutes N]
                                                   konfiguracja importu i lokalnego OCR
              harvest [--store <fragm>] [--since yyyy-MM-dd] [--max <N>] [--folders "A,B,C"]
                                                   zbierz foldery do korpusu (store/limit domyslnie z config.json;
                                                   --folders = tylko te nazwy, np. "Inbox,Sent Items,Archive")
              query [<tekst>] [--from d] [--to d] [--sender s] [--folder inbox|sent] [--limit N] [--all|--alerts]
                                                   szukaj (FTS + filtry); domyslnie POMIJA alerty; --all=z alertami, --alerts=tylko alerty
              query-content <tekst> [--limit N] [--raw]
                                                   szukaj w segmentach zalacznikow z kontekstem strony/slajdu/arkusza
              rebuild-content-index                odbuduj indeks segmentow z content_segments
              stats                                statystyki korpusu (liczby, korespondencja vs alerty, top nadawcy)
              reclassify                           przelicz kind (mail|alert) wg noise-rules.json dla calego korpusu
              analyze [--top N]                    wolumen nadawcow/folderow + podpowiedzi kandydatow na szum
              analyze-rules                        wklad kazdej reguly szumu (brutto/unikat) + zazebienie
              imap-add --host H --user <adres> [--name N] [--pass P]
                       [--port P --starttls]          dodaj konto IMAP (haslo chronione przez DPAPI)
              imap-list                            wypisz skonfigurowane konta IMAP
              imap-harvest [--account N] [--since yyyy-MM-dd] [--max N]   pobierz z IMAP do korpusu
              account add gmail                    polacz konto Gmail przez OAuth 2.0 w systemowej przegladarce
              account list                         wypisz polaczone konta
              account remove <id|adres>             odlacz konto i usun lokalny token OAuth
              gmail sync [--account A] [--full]     pelna lub przyrostowa synchronizacja Gmail API
              gmail status [--account A]            stan importu, liczby i bledy synchronizacji
              gmail cancel                         anuluj dzialajaca synchronizacje
              processing-status                    pokaz stan trwalej kolejki zalacznikow
              processing-run [--once]              uruchom osobny proces Worker
              processing-retry                     ponow zadania zakonczone statusem failed
              blob-gc [--dry-run]                   usun zaszyfrowane bloby bez aktywnych referencji
              selftest                             dowod: SQLCipher szyfruje, zly klucz odrzucony, FTS dziala

            Odblokowujesz w GUI (PIN + YubiKey). Klucz zyje tylko w RAM GUI. Bez tego korpus to szyfrogram.
            """);
        return 0;
    }

    static void PrintStatus()
    {
        string mode = Mode.Read() is { Length: > 0 } m ? m : "(nieustalony)";
        Console.WriteLine($"Katalog danych: {Paths.Base}{(Environment.GetEnvironmentVariable("KKR_MAILLENS_DIR") is { Length: > 0 } ? " (KKR_MAILLENS_DIR)" : " (domyslny)")}");
        Console.WriteLine($"Korpus: {(File.Exists(Paths.CorpusDb) ? "jest" : "BRAK - uzyj init")}");
        Console.WriteLine($"Tryb: {mode}");
        var s = Ipc.Request("STATUS");
        if (s is null) { Console.WriteLine("Status: brak agenta (GUI nie uruchomione)."); return; }
        var p = s.Split(' ');
        if (p[0] == "UNLOCKED" && p.Length >= 2 && int.TryParse(p[1], out int sec))
        {
            var t = TimeSpan.FromSeconds(sec);
            Console.WriteLine($"Status: ODBLOKOWANE (RAM w GUI), pozostalo {(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
        }
        else Console.WriteLine("Status: ZABLOKOWANE (odblokuj w GUI).");
    }

    static string PromptMasked(string prompt)
    {
        Console.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.ToString();
    }

    // Deleguja do wspoldzielonego Args (Query.cs) - jedno zrodlo logiki parsowania, bez ryzyka rozjazdu.
    static bool Flag(string[] args, string name) => Args.Flag(args, name);
    static string? GetStr(string[] args, string name) => Args.Str(args, name);
    static int GetInt(string[] args, string name, int fallback) => Args.Int(args, name, fallback);

    static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }
}

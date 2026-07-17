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
        if (changed) { cfg.Save(); Console.WriteLine($"Zapisano config: {Paths.ConfigFile}"); }

        Console.WriteLine($"Katalog danych: {Paths.Base}");
        Console.WriteLine($"store filter : '{cfg.StoreFilter}'{(cfg.StoreFilter.Length == 0 ? "  (wszystkie skrzynki)" : "")}");
        Console.WriteLine($"max/folder   : {cfg.MaxPerFolder}{(cfg.MaxPerFolder <= 0 ? "  (bez limitu)" : "")}");
        if (!changed) Console.WriteLine("Zmiana: config --store \"<fragment nazwy skrzynki>\" [--max <N>]  (0 = bez limitu)");
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
        var accts = ImapAccounts.Load();
        var a = new ImapAccount();
        if (Flag(args, "--gmail")) { a.Host = "imap.gmail.com"; a.Port = 993; a.UseSsl = true; }
        a.Host = GetStr(args, "--host") ?? a.Host;
        a.Port = GetInt(args, "--port", a.Port == 0 ? 993 : a.Port);
        a.User = GetStr(args, "--user") ?? "";
        if (Flag(args, "--starttls")) a.UseSsl = false;
        a.Name = GetStr(args, "--name") ?? a.User;
        if (string.IsNullOrEmpty(a.Host) || string.IsNullOrEmpty(a.User))
        { Console.Error.WriteLine("Podaj --host i --user (albo --gmail --user ...)."); return 1; }

        // Haslo tylko z --pass albo interaktywnie (nie z env - patrz uwaga o PIN wyzej).
        string? pass = GetStr(args, "--pass");
        if (string.IsNullOrEmpty(pass)) pass = PromptMasked($"Haslo / App-Password dla {a.User}: ");
        if (string.IsNullOrEmpty(pass)) { Console.Error.WriteLine("Brak hasla."); return 1; }
        a.SetPassword(pass);

        accts.Accounts.RemoveAll(x => string.Equals(x.Name, a.Name, StringComparison.OrdinalIgnoreCase));
        accts.Accounts.Add(a);
        accts.Save();
        Console.WriteLine($"Zapisano konto IMAP '{a.Name}' ({a.User}@{a.Host}:{a.Port}, ssl={a.UseSsl}). Haslo owiniete DPAPI.");
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
                int n = Imap.Harvest(a, since, max,
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
              config [--store <fragm>] [--max <N>] pokaz/ustaw konfiguracje harvestu (skrzynka + limit/folder;
                                                   store="" = wszystkie skrzynki, max<=0 = bez limitu)
              harvest [--store <fragm>] [--since yyyy-MM-dd] [--max <N>] [--folders "A,B,C"]
                                                   zbierz foldery do korpusu (store/limit domyslnie z config.json;
                                                   --folders = tylko te nazwy, np. "Inbox,Sent Items,Archive")
              query [<tekst>] [--from d] [--to d] [--sender s] [--folder inbox|sent] [--limit N] [--all|--alerts]
                                                   szukaj (FTS + filtry); domyslnie POMIJA alerty; --all=z alertami, --alerts=tylko alerty
              stats                                statystyki korpusu (liczby, korespondencja vs alerty, top nadawcy)
              reclassify                           przelicz kind (mail|alert) wg noise-rules.json dla calego korpusu
              analyze [--top N]                    wolumen nadawcow/folderow + podpowiedzi kandydatow na szum
              analyze-rules                        wklad kazdej reguly szumu (brutto/unikat) + zazebienie
              imap-add --host H --user <adres> [--name N] [--pass P]
                       [--port P --starttls]          dodaj konto IMAP (haslo chronione przez DPAPI)
              imap-list                            wypisz skonfigurowane konta IMAP
              imap-harvest [--account N] [--since yyyy-MM-dd] [--max N]   pobierz z IMAP do korpusu
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
}

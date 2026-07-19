using System.Globalization;
using System.Text;
using System.Windows.Forms;
using KKR.MailLens;

namespace KKR.MailLens.Gui;

/// <summary>
/// Maly cockpit nad rdzeniem CLI: odblokuj (PIN + YubiKey), status z odliczaniem, zablokuj,
/// szukanie (FTS) i harvest. Cala logika kryptograficzna/dostepu to te same klasy co w CLI
/// (Crypto/Session/Db/Query/YubiKey/Outlook) - GUI tylko je orkiestruje.
/// </summary>
sealed class MainForm : Form
{
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 10, FontStyle.Bold) };
    readonly TextBox _pin = new() { UseSystemPasswordChar = true, Width = 110, Margin = new Padding(0, 6, 4, 0) };
    readonly CheckBox _yubi = new() { Text = "YubiKey", Checked = true, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 9, 6, 0) };
    readonly ComboBox _ttl = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, Margin = new Padding(4, 6, 4, 0) };
    readonly Button _init = Btn("Inicjuj");
    readonly Button _unlock = Btn("Odblokuj");
    readonly Button _lock = Btn("Zablokuj");
    readonly TextBox _search = new() { Width = 200, Margin = new Padding(0, 6, 4, 0) };
    readonly ComboBox _searchKind = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115, Margin = new Padding(4, 6, 0, 0) };
    readonly Button _searchBtn = Btn("Szukaj");
    readonly Button _statsBtn = Btn("Statystyki");
    readonly ComboBox _range = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90, Margin = new Padding(4, 6, 4, 0) };
    readonly Button _harvestBtn = Btn("Harvest");
    readonly Button _rulesBtn = Btn("Reguły szumu");
    readonly Button _gmailBtn = Btn("Gmail");

    // Jednakowa szerokosc = przyciski tworza pionowe "tory"; staly rozmiar = brak drgania.
    const int BtnW = 100;
    static Button Btn(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Width = BtnW,
        Height = 28,
        Margin = new Padding(4, 4, 0, 4),
    };
    readonly ProgressBar _prog = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Continuous, Visible = false, Margin = new Padding(0, 2, 0, 2) };
    readonly TextBox _out = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    readonly Agent _agent = new();
    readonly NotifyIcon _tray = new() { Icon = System.Drawing.SystemIcons.Shield, Text = "KKR MailLens", Visible = false };
    CancellationTokenSource? _activeHarvest;
    bool _reallyExit, _balloonShown;
    bool _busy; // straz przed nakladaniem operacji (np. Enter w _pin/_search) - SetBusy wylacza kontrolki, to jest pas i szelki

    public MainForm()
    {
        Text = "KKR MailLens";
        Width = 1000; Height = 600;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 440);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); // status
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76)); // siatka kontrolek (STALA wysokosc - brak drgania)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); // progress bar
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // output

        root.Controls.Add(_status, 0, 0);

        // Uklad wg mockupu: etykieta | pole | srodek | Sesja/Zakres | combo | STREFA-przyciskow(stala).
        // Strefa przyciskow ma ta sama szerokosc w obu wierszach -> gorne 3 i dolne 2 wypelniaja ja rowno
        // (dolne szersze), prawe krawedzie sie pokrywaja, brak pustej kratki. Stale rozmiary => brak drgania.
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 0 etykieta (PIN/Szukaj)
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); // 1 pole
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // 2 srodek (YubiKey / Szukaj+Statystyki) + luz
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 3 etykieta Sesja/Zakres
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130)); // 4 combo
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320)); // 5 strefa przyciskow (stala)
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        static Label Lab(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 6, 0) };

        _pin.Anchor = AnchorStyles.Left | AnchorStyles.Right; _pin.Margin = new Padding(0, 6, 0, 0);
        _search.Anchor = AnchorStyles.Left | AnchorStyles.Right; _search.Margin = new Padding(0, 6, 0, 0);
        _yubi.Anchor = AnchorStyles.Left; _yubi.Margin = new Padding(6, 0, 0, 0);
        _ttl.Anchor = AnchorStyles.Left | AnchorStyles.Right; _ttl.Margin = new Padding(0, 6, 0, 0);
        _range.Anchor = AnchorStyles.Left | AnchorStyles.Right; _range.Margin = new Padding(0, 6, 0, 0);
        _ttl.Items.AddRange(new object[] { "5h", "12h", "24h" }); _ttl.SelectedIndex = 0;
        _range.Items.AddRange(new object[] { "3 dni", "Dzis", "Od ostatniego", "7 dni", "30 dni", "Ten rok", "Wszystko" }); _range.SelectedIndex = 0; // domyslnie 3 dni (zachodzi na siebie - brak dziur)
        _searchKind.Items.AddRange(new object[] { "Wiadomości", "Załączniki", "Hybrydowe", "Wszystko" }); _searchKind.SelectedIndex = 0;

        // srodek (col2): YubiKey w gorze, Szukaj+Statystyki w dole; reszta col2 to luz (Percent)
        var unlockMid = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        unlockMid.Controls.Add(_yubi);
        var searchMid = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = new Padding(0) };
        searchMid.Controls.AddRange(new Control[] { _searchKind, _searchBtn, _statsBtn });

        // strefa przyciskow: rowne kolumny wypelniaja stale 320px -> obie grupy tej samej szerokosci
        foreach (var b in new[] { _init, _unlock, _lock, _harvestBtn, _rulesBtn, _gmailBtn })
        { b.Anchor = AnchorStyles.Left | AnchorStyles.Right; b.Margin = new Padding(2, 4, 2, 4); }
        static TableLayoutPanel BtnZone(int cols)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = cols, RowCount = 1, Margin = new Padding(0) };
            for (int i = 0; i < cols; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
            return t;
        }
        var unlockZone = BtnZone(3); unlockZone.Controls.AddRange(new Control[] { _init, _unlock, _lock });
        var searchZone = BtnZone(3); searchZone.Controls.AddRange(new Control[] { _harvestBtn, _gmailBtn, _rulesBtn });

        grid.Controls.Add(Lab("PIN:"), 0, 0);
        grid.Controls.Add(_pin, 1, 0);
        grid.Controls.Add(unlockMid, 2, 0);
        grid.Controls.Add(Lab("Sesja:"), 3, 0);
        grid.Controls.Add(_ttl, 4, 0);
        grid.Controls.Add(unlockZone, 5, 0);
        grid.Controls.Add(Lab("Szukaj:"), 0, 1);
        grid.Controls.Add(_search, 1, 1);
        grid.Controls.Add(searchMid, 2, 1);
        grid.Controls.Add(Lab("Zakres:"), 3, 1);
        grid.Controls.Add(_range, 4, 1);
        grid.Controls.Add(searchZone, 5, 1);

        root.Controls.Add(grid, 0, 1);
        root.Controls.Add(_prog, 0, 2);
        root.Controls.Add(_out, 0, 3);
        Controls.Add(root);

        _init.Click += async (_, _) => await DoInit();
        _unlock.Click += async (_, _) => await DoUnlock();
        _lock.Click += (_, _) => LockSession("Zablokowano - klucz usunięty z RAM.");
        _searchBtn.Click += async (_, _) => await DoSearch();
        _statsBtn.Click += async (_, _) => await RunCapture(() => Query.Stats(RamSession.Key!), "statystyki...");
        _harvestBtn.Click += async (_, _) => await DoHarvest();
        _gmailBtn.Click += (_, _) => OpenGmail();
        _rulesBtn.Click += async (_, _) => await OpenNoiseRules();
        _search.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoSearch(); } };
        _pin.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await DoUnlock(); } };

        Session.Lock(); // RAM-model: nic nie dziedziczymy z dysku, startujemy zablokowani
        _agent.Start();  // serwer pipe: udostepnia klucz z RAM lokalnemu CLI tego samego uzytkownika

        // Tray: minimalizacja/zamkniecie chowa do zasobnika (sesja zyje w tle); wyjscie tylko z menu.
        var menu = new ContextMenuStrip();
        menu.Items.Add("Pokaz okno", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Zablokuj sesje", null, (_, _) => LockSession("Zablokowano sesję z zasobnika."));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Zamknij (konczy sesje)", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray(); };

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        RefreshMeta();
        RefreshStatus();
        _out.Text = (Setup.IsInitialized
            ? "Gotowe. Odblokuj (PIN" + (Mode.Yubi ? " + wepnij YubiKey" : "") + "), potem Szukaj / Harvest."
            : "Korpus niezainicjowany. Wpisz PIN, zaznacz YubiKey (2FA) i kliknij Inicjuj - utworzy zaszyfrowana baze. Potem Harvest.")
            + Environment.NewLine + "Katalog danych: " + Paths.Base + Environment.NewLine;
    }

    async Task DoInit()
    {
        if (_busy) return;
        string pin = _pin.Text;
        if (pin.Length == 0) { Log("Wpisz PIN w polu, potem Inicjuj."); return; }
        string? confirm = PromptPin("Potwierdz PIN");
        if (confirm is null) return;
        if (confirm != pin) { Log("PIN sie nie zgadza - sprobuj jeszcze raz."); return; }
        bool wantYubi = _yubi.Checked;
        // Nadpisanie dotyczy KAZDEJ istniejacej bazy (nawet gdy mode.txt zniknal i IsInitialized=false).
        bool dbExists = File.Exists(Paths.CorpusDb);
        bool force = Setup.IsInitialized || dbExists;
        if (dbExists && MessageBox.Show(this,
            "Korpus juz istnieje. Ponowna inicjacja SKASUJE dotychczasowe dane (trzeba bedzie ponownie zharvestowac). Kontynuowac?",
            "Uwaga", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        SetBusy(true, wantYubi ? "licze YubiKey / tworze baze..." : "tworze baze...");
        try
        {
            var r = await Task.Run(() => Setup.Init(pin, wantYubi, force));
            _pin.Clear();
            if (r.Error != null) { Log(r.Error); return; }
            int ttl = TtlHours();
            RamSession.Set(r.KeyHex!, TimeSpan.FromHours(ttl)); // klucz w RAM (nie na dysku)
            Session.Lock(); // upewnij sie ze nic nie zostaje na dysku
            Log($"Zainicjowano zaszyfrowany korpus [{(wantYubi ? "PIN + YubiKey" : "PIN")}], klucz w RAM na {ttl}h. Teraz kliknij Harvest.");
        }
        finally { SetBusy(false); RefreshMeta(); RefreshStatus(); }
    }

    string? PromptPin(string title)
    {
        using var f = new Form { Text = title, Width = 300, Height = 150, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false };
        var tb = new TextBox { UseSystemPasswordChar = true, Left = 15, Top = 20, Width = 255 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 110, Top = 60, Width = 75 };
        var cancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, Left = 195, Top = 60, Width = 75 };
        tb.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ok.PerformClick(); } };
        f.Controls.Add(tb); f.Controls.Add(ok); f.Controls.Add(cancel);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
    }

    int _pollN;
    void OnTick()
    {
        if (_activeHarvest is { IsCancellationRequested: false } active && !RamSession.Unlocked)
        {
            active.Cancel();
            _lock.Enabled = false;
            Log("Sesja wygasła lub została zablokowana — anuluję aktywny harvest.");
        }
        RefreshStatus();
        // co ~5s: jesli tryb pin+yubi i odblokowane, sprawdz czy klucz nadal wpiety -> auto-lock po wyjeciu
        if (RamSession.Unlocked && Mode.Yubi && (++_pollN % 5 == 0) && !YubiKey.TryInfo(out _))
        {
            LockSession("YubiKey wyjęty — sesja zablokowana (auto-lock).");
        }
    }

    void LockSession(string message)
    {
        _activeHarvest?.Cancel();
        RamSession.Clear();
        Session.Lock();
        _pin.Clear();
        _lock.Enabled = false;
        RefreshStatus();
        Log(message);
    }

    void HideToTray()
    {
        _tray.Visible = true;
        Hide();
        if (!_balloonShown)
        {
            _tray.ShowBalloonTip(3000, "KKR MailLens", "Dziala w tle - sesja aktywna. Dwuklik ikony = pokaz.", ToolTipIcon.Info);
            _balloonShown = true;
        }
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        _tray.Visible = false;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X / Alt+F4 = schowaj do traya (nie ubijaj sesji przypadkiem); realne wyjscie tylko przez menu traya.
        if (e.CloseReason == CloseReason.UserClosing && !_reallyExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        _activeHarvest?.Cancel();
        RamSession.Clear(); // zeroizacja best-effort przy realnym wyjsciu
        _agent.Dispose();   // zatrzymaj serwer pipe
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosing(e);
    }

    async Task OpenNoiseRules()
    {
        if (_busy) return;
        var rules = NoiseRules.Load();
        using var f = new Form
        {
            Text = "Reguły szumu (mail vs alert) - deterministyczne",
            Width = 560, Height = 560, StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.SizableToolWindow, MinimizeBox = false, MaximizeBox = false
        };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        TextBox Box(string val) => new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), Text = val };
        static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };

        var tbSenders = Box(string.Join(Environment.NewLine, rules.NoiseSenders));
        var tbContains = Box(string.Join(Environment.NewLine, rules.NoiseSenderContains));
        var tbFolders = Box(string.Join(Environment.NewLine, rules.NoiseFolders));

        root.Controls.Add(Lbl("noiseSenders - dokładny adres nadawcy (1 na linię):"), 0, 0);
        root.Controls.Add(tbSenders, 0, 1);
        root.Controls.Add(Lbl("noiseSenderContains - fragment adresu LUB nazwy:"), 0, 2);
        root.Controls.Add(tbContains, 0, 3);
        root.Controls.Add(Lbl("noiseFolders - nazwa folderu:"), 0, 4);
        root.Controls.Add(tbFolders, 0, 5);

        var btnRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var bCancel = new Button { Text = "Anuluj", DialogResult = DialogResult.Cancel, AutoSize = true };
        var bSave = new Button { Text = "Zapisz", AutoSize = true };
        var bSaveRe = new Button { Text = "Zapisz + Reclassify", AutoSize = true };
        btnRow.Controls.Add(bCancel); btnRow.Controls.Add(bSaveRe); btnRow.Controls.Add(bSave);
        root.Controls.Add(btnRow, 0, 6);
        f.Controls.Add(root);
        f.CancelButton = bCancel;

        void Apply()
        {
            rules.NoiseSenders = ParseLines(tbSenders.Text);
            rules.NoiseSenderContains = ParseLines(tbContains.Text);
            rules.NoiseFolders = ParseLines(tbFolders.Text);
            rules.Save();
        }
        bool reclass = false;
        bSave.Click += (_, _) => { Apply(); f.DialogResult = DialogResult.OK; };
        bSaveRe.Click += (_, _) => { Apply(); reclass = true; f.DialogResult = DialogResult.OK; };

        if (f.ShowDialog(this) != DialogResult.OK) return;
        Log($"Reguły szumu zapisane ({Paths.NoiseRulesFile}).");
        if (!reclass) return;

        string? key = RamSession.Key;
        if (key is null) { Log("Reclassify pominięte - odblokuj sesję."); return; }
        SetBusy(true, "reclassify...");
        try
        {
            string msg = await Task.Run(() =>
            {
                using var c = Db.Open(key, create: false);
                Db.EnsureSchema(c);
                int a = Corpus.Reclassify(c);
                long t = Corpus.Count(c);
                return $"Reclassify: alert={a}, mail={t - a} (z {t}).";
            });
            Log(msg);
        }
        catch (Exception ex) { Log("Błąd reclassify: " + ex.Message); }
        finally { SetBusy(false); RefreshStatus(); }
    }

    static List<string> ParseLines(string text)
    {
        var outp = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var s = raw.Trim();
            if (s.Length > 0) outp.Add(s);
        }
        return outp;
    }

    int TtlHours() => _ttl.SelectedItem?.ToString() switch { "12h" => 12, "24h" => 24, _ => 5 };

    DateTime? LastHarvestSince(string key)
    {
        try
        {
            using var c = Db.Open(key, create: false);
            var lh = Corpus.LastHarvest(c);
            if (lh != null && DateTime.TryParseExact(lh, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return d;
        }
        catch { }
        return null; // brak -> pelny pull
    }

    string[] BuildQueryArgs()
    {
        // fraza za separatorem '--': tekst zaczynajacy sie od '--' nie zostanie potraktowany jak flaga
        string t = _search.Text.Trim();
        return t.Length == 0 ? new[] { "query", "--limit", "50" } : new[] { "query", "--limit", "50", "--", t };
    }

    string[] BuildContentQueryArgs()
    {
        string text = _search.Text.Trim();
        return new[] { "query-content", text, "--limit", "50" };
    }

    async Task DoSearch()
    {
        string kind = _searchKind.SelectedItem?.ToString() ?? "Wiadomości";
        if (kind != "Wiadomości" && string.IsNullOrWhiteSpace(_search.Text))
        {
            Log("Podaj frazę do wyszukania w zawartości załączników.");
            return;
        }

        await RunCapture(() =>
        {
            if (kind is "Wiadomości" or "Wszystko")
            {
                if (kind == "Wszystko") Console.WriteLine("=== WIADOMOŚCI ===");
                Query.Run(RamSession.Key!, BuildQueryArgs());
            }
            if (kind is "Załączniki" or "Wszystko")
            {
                if (kind == "Wszystko") Console.WriteLine("=== ZAŁĄCZNIKI I TRANSKRYPCJE ===");
                ContentSearch.Run(RamSession.Key!, BuildContentQueryArgs());
            }
            if (kind == "Hybrydowe") RunSemanticSearch();
        }, "szukam...");
    }

    void RunSemanticSearch()
    {
        AppConfig config = AppConfig.Load();
        if (!config.SemanticEnabled)
        {
            Console.Error.WriteLine("Wyszukiwanie semantyczne jest wyłączone. Włącz je poleceniem config --semantic-enabled true.");
            return;
        }
        using var connection = Db.Open(RamSession.Key!, create: false);
        Db.EnsureSchema(connection);
        using IEmbeddingProvider provider = SemanticServices.CreateProvider(config);
        SemanticQueryResult result = SemanticSearch.SearchAsync(connection, provider, _search.Text.Trim(), 50,
            hybrid: true, Math.Clamp(config.SemanticMaxCandidates, 100, 250_000)).GetAwaiter().GetResult();
        foreach (SemanticSearchHit item in result.Hits)
        {
            ContentSearchHit hit = item.Hit;
            string similarity = item.Similarity is null ? "" : $", podobieństwo {item.Similarity.Value:0.000}";
            Console.WriteLine($"{hit.Received}  {hit.Sender}  [{item.Channels}{similarity}]");
            Console.WriteLine($"    {hit.Subject}");
            Console.WriteLine($"    {SearchLocation(hit)}{hit.Filename}");
            if (hit.Snippet.Length > 0) Console.WriteLine($"    | {hit.Snippet}");
        }
        if (result.CandidateLimitReached)
            Console.WriteLine($"UWAGA: ranking ograniczono do {config.SemanticMaxCandidates} najnowszych segmentów.");
        Console.WriteLine(result.Hits.Count == 0 ? "(brak trafień)" : $"-- {result.Hits.Count} trafień");
    }

    static string SearchLocation(ContentSearchHit hit)
    {
        if (hit.PageNumber is not null) return $"strona {hit.PageNumber}: ";
        if (hit.SlideNumber is not null) return $"slajd {hit.SlideNumber}: ";
        if (!string.IsNullOrWhiteSpace(hit.SheetName)) return $"arkusz {hit.SheetName}: ";
        if (hit.StartMs is not null) return $"{Timestamp(hit.StartMs.Value)}–{Timestamp(hit.EndMs ?? hit.StartMs.Value)}: ";
        return "";
    }

    static string Timestamp(long milliseconds)
    {
        long seconds = Math.Max(0, milliseconds) / 1000;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    void OpenGmail()
    {
        if (RamSession.Key is null) { Log("Najpierw odblokuj."); return; }
        using var form = new GmailManagerForm(() => RamSession.Key);
        form.ShowDialog(this);
    }

    async Task DoUnlock()
    {
        if (_busy) return;
        string pin = _pin.Text;
        if (pin.Length == 0) { Log("Podaj PIN."); return; }
        bool wantYubi = Mode.Yubi; // sticky - ustalone przy inicjacji
        SetBusy(true, wantYubi ? "licze YubiKey..." : "odblokowuje...");
        try
        {
            var (err, key) = await Task.Run<(string?, string?)>(() =>
            {
                if (!Setup.IsInitialized || !File.Exists(Paths.CorpusDb))
                    return ("Korpus niezainicjowany - najpierw kliknij Inicjuj.", null);
                byte[]? resp = null;
                if (wantYubi)
                {
                    if (!YubiKey.TryInfo(out var info)) return ($"YubiKey niewykryty ({info}). Wepnij klucz.", null);
                    try { resp = YubiKey.ChallengeResponse(Crypto.ReadSaltOrThrow()); }
                    catch (Exception ex) { return ("Blad YubiKey: " + ex.Message, null); }
                }
                string k;
                try { k = Crypto.DeriveKeyHex(pin, resp); }
                catch (Exception ex) { return ("Nie moge zderywowac klucza: " + ex.Message, null); }
                if (!Db.VerifyKey(k)) return ("Zly PIN/YubiKey - nie pasuje do korpusu.", null);
                return (null, k);
            });
            _pin.Clear();
            if (err != null) { Log(err); return; }
            int ttl = TtlHours();
            RamSession.Set(key!, TimeSpan.FromHours(ttl)); // klucz w RAM
            Session.Lock(); // dysk czysty
            Log($"Odblokowano [{(wantYubi ? "PIN + YubiKey" : "PIN")}], klucz w RAM na {ttl}h.");
            try
            {
                SessionCredentialMigrationResult migration = await SessionCredentialMigration.RunAsync(key!);
                if (migration.GmailTokens + migration.ImapPasswords > 0)
                    Log($"Zmigrowano poświadczenia do ochrony sesyjnej: Gmail={migration.GmailTokens}, IMAP={migration.ImapPasswords}.");
                if (migration.Failures > 0)
                    Log($"UWAGA: nie udało się zmigrować poświadczeń: {migration.Failures}. Połącz konto ponownie.");
            }
            catch (Exception ex) { Log("UWAGA: migracja poświadczeń nieudana: " + ex.GetType().Name); }
        }
        finally { SetBusy(false); RefreshStatus(); }
    }

    async Task DoHarvest()
    {
        if (_busy) return;
        string? key = RamSession.Key;
        if (key is null) { Log("Najpierw odblokuj."); return; }
        string zakres = (string)_range.SelectedItem!;
        DateTime? since = zakres switch
        {
            "Dzis" => DateTime.Today,
            "3 dni" => DateTime.Today.AddDays(-3),
            "7 dni" => DateTime.Today.AddDays(-7),
            "30 dni" => DateTime.Today.AddDays(-30),
            "Ten rok" => new DateTime(DateTime.Today.Year, 1, 1),
            "Od ostatniego" => LastHarvestSince(key),
            _ => (DateTime?)null, // Wszystko
        };
        if (zakres == "Od ostatniego" && since is null) Log("Brak poprzedniego harvestu - pelny pull.");
        var cfg = AppConfig.Load(); // store/limit z config.json (nie zaszyte); ta sama sciezka co CLI
        string storeDisp = cfg.StoreFilter.Length == 0 ? "wszystkie skrzynki" : $"store~{cfg.StoreFilter}";
        Log($"Harvest [{zakres}] wszystkie foldery bez usunietych/draft ({storeDisp})...");
        SetBusy(true, $"harvest [{zakres}]...");
        _lock.Enabled = true;
        _prog.Value = 0; _prog.Visible = true;
        using var cancellation = new CancellationTokenSource();
        _activeHarvest = cancellation;
        try
        {
            string msg = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Action<int, int> prog = (done, total) => { try { BeginInvoke(() => UpdateProgress(zakres, done, total)); } catch { } };
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                using var c = Db.Open(key, create: false); // init gwarantuje istnienie bazy; nie tworzymy po cichu
                Db.EnsureSchema(c);
                int ins = 0, upd = 0;
                using (var ol = new Outlook())
                    ol.HarvestMail(cfg.StoreFilter, since, cfg.EffectiveMax, _ => { }, prog,
                        batch =>
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            var st = Corpus.Upsert(c, batch, stamp, cancellation.Token);
                            ins += st.Inserted;
                            upd += st.Updated;
                        }, cancellationToken: cancellation.Token); // zapis porcjami
                return $"Harvest: +{ins} nowych, {upd} zaktualizowanych. Korpus: {Corpus.Count(c)} maili.";
            }, cancellation.Token);
            Log(msg);
        }
        catch (OperationCanceledException)
        {
            Log("Harvest anulowany po zablokowaniu sesji; wcześniej zapisane porcje pozostają w korpusie.");
        }
        catch (Exception ex) { Log("Blad harvest: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_activeHarvest, cancellation)) _activeHarvest = null;
            _prog.Visible = false;
            SetBusy(false);
            RefreshStatus();
        }
    }

    void UpdateProgress(string zakres, int done, int total)
    {
        if (total > 0)
        {
            int pct = Math.Min(100, done * 100 / total);
            _prog.Style = ProgressBarStyle.Continuous;
            _prog.Maximum = total;
            _prog.Value = Math.Min(done, total);
            _status.Text = $"harvest [{zakres}]... {pct}% ({done}/{total})";
        }
        else
        {
            _prog.Style = ProgressBarStyle.Marquee; // nieznana liczba -> pasek "chodzacy"
            _status.Text = $"harvest [{zakres}]... {done}";
        }
    }

    // Console.Out/Error sa globalne dla procesu: dwa rownolegle przechwycenia przeplotlyby save/restore
    // i zostawily Console.Out na martwym StringWriterze - stad serializacja calego bloku podmiany.
    static readonly object ConsoleCaptureLock = new();

    async Task RunCapture(Action a, string busy)
    {
        if (_busy) return;
        if (RamSession.Key is null) { Log("Najpierw odblokuj."); return; }
        if (!File.Exists(Paths.CorpusDb)) { Log("Korpus pusty - najpierw Harvest."); return; }
        SetBusy(true, busy);
        try
        {
            string txt = await Task.Run(() =>
            {
                lock (ConsoleCaptureLock)
                {
                    var sw = new StringWriter();
                    var oldOut = Console.Out; var oldErr = Console.Error;
                    Console.SetOut(sw); Console.SetError(sw); // przechwyc tez bledy (Query.Run pisze na stderr)
                    try { a(); } finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
                    return sw.ToString();
                }
            });
            _out.Text = txt.Length == 0 ? "(brak wyniku)" : txt.Replace("\n", Environment.NewLine);
        }
        catch (Exception ex) { Log("Blad: " + ex.Message); }
        finally { SetBusy(false); }
    }

    void SetBusy(bool busy, string? msg = null)
    {
        _busy = busy;
        _init.Enabled = _unlock.Enabled = _lock.Enabled = _searchBtn.Enabled = _statsBtn.Enabled = _harvestBtn.Enabled
            = _range.Enabled = _ttl.Enabled = _rulesBtn.Enabled = _gmailBtn.Enabled = _searchKind.Enabled = !busy;
        _pin.Enabled = _search.Enabled = !busy; // Enter w tych polach startuje operacje - tez musza byc zablokowane
        UseWaitCursor = busy;
        if (busy && msg != null) _status.Text = msg;
    }

    // init-state i tryb zmieniaja sie tylko przy Init - cache'ujemy, by tick 1s nie robil dysk-I/O
    // (File.Exists + ReadAllText) w kolko, tez zminimalizowany do traya. Tick liczy tylko odliczanie RAM.
    bool _initCached;
    string _modeCached = "(nieustalony)";
    void RefreshMeta()
    {
        _initCached = Setup.IsInitialized;
        _modeCached = Mode.Read() is { Length: > 0 } m ? m : "(nieustalony)";
    }

    void RefreshStatus()
    {
        bool init = _initCached;
        string mode = _modeCached;
        bool unlocked = RamSession.Unlocked;

        if (!init)
        {
            _status.Text = $"● NIEZAINICJOWANY  -  ustaw PIN, zaznacz YubiKey, kliknij Inicjuj   [{Paths.Base}]";
            _status.ForeColor = Color.DarkOrange;
        }
        else if (unlocked)
        {
            var r = RamSession.Remaining;
            _status.Text = $"● ODBLOKOWANE [{mode}]  (RAM)  -  pozostalo {(int)r.TotalHours:00}:{r.Minutes:00}:{r.Seconds:00}";
            _status.ForeColor = Color.ForestGreen;
        }
        else
        {
            _status.Text = $"● ZABLOKOWANE [{mode}]";
            _status.ForeColor = Color.Firebrick;
        }

        // przyciski zalezne od stanu (o ile nie w trakcie operacji). En() ustawia TYLKO gdy sie zmienia -
        // brak zbednych invalidacji layoutu co sekunde.
        if (!UseWaitCursor)
        {
            En(_init, true);
            En(_yubi, !init);
            En(_unlock, init && !unlocked);
            En(_lock, unlocked);
            En(_ttl, !unlocked);
            En(_searchBtn, unlocked); En(_statsBtn, unlocked); En(_harvestBtn, unlocked); En(_range, unlocked);
            En(_gmailBtn, unlocked); En(_searchKind, unlocked);
        }

        _tray.Text = unlocked
            ? $"KKR MailLens - odblokowane ({(int)RamSession.Remaining.TotalMinutes} min)"
            : "KKR MailLens - zablokowane";
    }

    static void En(Control c, bool v) { if (c.Enabled != v) c.Enabled = v; }

    void Log(string line)
    {
        _out.AppendText((_out.TextLength > 0 ? Environment.NewLine : "") + line);
    }
}

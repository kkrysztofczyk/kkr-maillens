using KKR.MailLens;

namespace KKR.MailLens.Gui;

sealed class MailboxManagerForm : Form
{
    readonly Func<string?> _keyProvider;
    readonly MailboxPipelineCoordinator _pipeline = new();
    readonly DataGridView _mailboxes = Grid(multiSelect: true);
    readonly DataGridView _queue = Grid(multiSelect: false);
    readonly Label _configuredMetric = MetricValue();
    readonly Label _messagesMetric = MetricValue();
    readonly Label _runMetric = MetricValue();
    readonly Label _processingMetric = MetricValue();
    readonly Label _status = MailboxUi.Label(
        "Gotowe.",
        9,
        FontStyle.Regular,
        MailboxUi.Muted);
    readonly Label _sectionTitle = MailboxUi.Label(
        "Skonfigurowane skrzynki",
        12,
        FontStyle.Bold);
    readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Continuous,
        Minimum = 0,
        Maximum = 1000,
        Value = 0,
        Height = 7,
    };
    readonly Button _add = MailboxUi.PrimaryButton("+ Dodaj skrzynkę");
    readonly Button _refresh = MailboxUi.SecondaryButton("Odśwież");
    readonly Button _importSelected = MailboxUi.PrimaryButton("Importuj wybrane");
    readonly Button _importAll = MailboxUi.SecondaryButton("Importuj wszystkie");
    readonly Button _toggle = MailboxUi.SecondaryButton("Włącz / wyłącz");
    readonly Button _cancel = MailboxUi.DangerButton("Anuluj kolejkę");
    readonly CheckBox _forceFull = new()
    {
        Text = "Pełny import",
        AutoSize = true,
        ForeColor = MailboxUi.Muted,
        Font = new Font("Segoe UI", 9),
        Margin = new Padding(10, 8, 4, 0),
    };
    readonly IReadOnlyDictionary<ProcessingStageKind, Label> _stageLabels;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    MailboxDashboardSnapshot? _snapshot;
    CancellationTokenSource? _pipelineCancellation;
    CancellationTokenSource? _addCancellation;
    Task? _pipelineTask;
    bool _refreshing;
    bool _adding;
    bool _sessionCancellationReported;

    public MailboxManagerForm(Func<string?> keyProvider)
    {
        _keyProvider = keyProvider;
        Text = "KKR MailLens — Skrzynki";
        BackColor = MailboxUi.Canvas;
        ForeColor = MailboxUi.Text;
        Font = new Font("Segoe UI", 9);
        Width = 1220;
        Height = 800;
        MinimumSize = new Size(980, 650);
        StartPosition = FormStartPosition.CenterParent;

        ConfigureMailboxGrid();
        ConfigureQueueGrid();
        _stageLabels = CreateStageLabels();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24),
            BackColor = MailboxUi.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildMetrics(), 0, 1);
        root.Controls.Add(BuildMailboxCard(), 0, 2);
        root.Controls.Add(BuildQueueCard(), 0, 3);
        root.Controls.Add(BuildFooter(), 0, 4);
        Controls.Add(root);

        var addMenu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 9),
            ShowImageMargin = false,
            BackColor = MailboxUi.Surface,
        };
        addMenu.Items.Add("Gmail — OAuth 2.0", null, async (_, _) => await AddGmailAsync());
        addMenu.Items.Add("IMAP — serwer i hasło aplikacji", null, async (_, _) => await AddImapAsync());
        addMenu.Items.Add("Outlook — skrzynka lub podmontowany PST", null, async (_, _) => await AddOutlookAsync());

        _add.Click += (_, _) => addMenu.Show(
            _add,
            new Point(0, _add.Height + 4));
        _refresh.Click += async (_, _) => await RefreshDashboardAsync();
        _importSelected.Click += async (_, _) => await QueueSourcesAsync(
            SelectedSourceIds());
        _importAll.Click += async (_, _) => await QueueSourcesAsync(
            _snapshot?.Sources
                .Where(item => item.Source.Enabled)
                .Select(item => item.Source.Id)
                .ToArray() ?? []);
        _toggle.Click += async (_, _) => await ToggleSelectedAsync();
        _cancel.Click += (_, _) => CancelPipeline();
        _mailboxes.SelectionChanged += (_, _) => UpdateActions();
        _mailboxes.CellDoubleClick += async (_, _) => await ToggleSelectedAsync();
        Shown += async (_, _) =>
        {
            await RefreshDashboardAsync();
            if (_snapshot?.ActiveRun is { } active)
                await EnsurePipelineRunningAsync(active.Id);
        };
        _timer.Tick += async (_, _) =>
        {
            CheckSession();
            await RefreshDashboardAsync(quiet: true);
        };
        _timer.Start();
        UpdateActions();
    }

    static DataGridView Grid(bool multiSelect)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoGenerateColumns = false,
            MultiSelect = multiSelect,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };
        MailboxUi.StyleGrid(grid);
        return grid;
    }

    static Label MetricValue()
        => MailboxUi.Label("—", 18, FontStyle.Bold);

    Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var text = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        text.Controls.Add(MailboxUi.Label(
            "Skrzynki pocztowe",
            22,
            FontStyle.Bold));
        text.Controls.Add(MailboxUi.Label(
            "Dodawaj źródła, ustawiaj kolejność i obserwuj import oraz indeksowanie.",
            9.5f,
            FontStyle.Regular,
            MailboxUi.Muted));

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Margin = new Padding(0, 8, 0, 0),
        };
        actions.Controls.Add(_refresh);
        actions.Controls.Add(_add);
        header.Controls.Add(text, 0, 0);
        header.Controls.Add(actions, 1, 0);
        return header;
    }

    Control BuildMetrics()
    {
        var metrics = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 14),
        };
        for (int index = 0; index < 4; index++)
            metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        metrics.Controls.Add(MetricCard(
            "AKTYWNE SKRZYNKI",
            _configuredMetric), 0, 0);
        metrics.Controls.Add(MetricCard(
            "WIADOMOŚCI",
            _messagesMetric), 1, 0);
        metrics.Controls.Add(MetricCard(
            "BIEŻĄCY PRZEBIEG",
            _runMetric), 2, 0);
        metrics.Controls.Add(MetricCard(
            "PRZETWARZANIE",
            _processingMetric), 3, 0);
        return metrics;
    }

    static Control MetricCard(string caption, Label value)
    {
        Panel card = MailboxUi.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 10, 0);
        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };
        stack.Controls.Add(MailboxUi.Label(
            caption,
            8,
            FontStyle.Bold,
            MailboxUi.Muted));
        value.Margin = new Padding(0, 5, 0, 0);
        stack.Controls.Add(value);
        card.Controls.Add(stack);
        return card;
    }

    Control BuildMailboxCard()
    {
        Panel card = MailboxUi.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0, 0, 0, 12);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var toolbar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _sectionTitle.Anchor = AnchorStyles.Left;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        actions.Controls.Add(_forceFull);
        actions.Controls.Add(_toggle);
        actions.Controls.Add(_importAll);
        actions.Controls.Add(_importSelected);
        toolbar.Controls.Add(_sectionTitle, 0, 0);
        toolbar.Controls.Add(actions, 1, 0);
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_mailboxes, 0, 1);
        card.Controls.Add(layout);
        return card;
    }

    Control BuildQueueCard()
    {
        Panel card = MailboxUi.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = new Padding(0);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
        };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Label title = MailboxUi.Label(
            "Kolejka i przetwarzanie",
            12,
            FontStyle.Bold);
        title.Anchor = AnchorStyles.Left;
        _cancel.Anchor = AnchorStyles.Right;
        heading.Controls.Add(title, 0, 0);
        heading.Controls.Add(_cancel, 1, 0);
        layout.Controls.Add(heading, 0, 0);

        var stages = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = true,
            Margin = new Padding(0, 4, 0, 6),
        };
        foreach (ProcessingStageKind stage in Enum.GetValues<ProcessingStageKind>()
                     .Where(value => value != ProcessingStageKind.Other))
        {
            Label label = _stageLabels[stage];
            var badge = new Panel
            {
                BackColor = MailboxUi.SoftSlate,
                Width = 172,
                Height = 36,
                Margin = new Padding(0, 0, 8, 0),
                Padding = new Padding(10, 8, 10, 0),
            };
            badge.Controls.Add(label);
            stages.Controls.Add(badge);
        }
        layout.Controls.Add(stages, 0, 1);
        layout.Controls.Add(_queue, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    Control BuildFooter()
    {
        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Margin = new Padding(0, 8, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        _status.Anchor = AnchorStyles.Left;
        _progress.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(_progress, 1, 0);
        return footer;
    }

    void ConfigureMailboxGrid()
    {
        _mailboxes.Columns.Add(Column("provider", "Typ", 90));
        _mailboxes.Columns.Add(FillColumn("name", "Skrzynka", 26));
        _mailboxes.Columns.Add(FillColumn("identity", "Konto / źródło", 34));
        _mailboxes.Columns.Add(Column("enabled", "Stan", 90));
        _mailboxes.Columns.Add(Column("messages", "Wiadomości", 100));
        _mailboxes.Columns.Add(FillColumn("last", "Ostatni import", 24));
        _mailboxes.Columns.Add(Column("errors", "Błędy", 70));
    }

    void ConfigureQueueGrid()
    {
        _queue.Columns.Add(Column("position", "#", 46));
        _queue.Columns.Add(Column("provider", "Typ", 90));
        _queue.Columns.Add(FillColumn("name", "Skrzynka", 28));
        _queue.Columns.Add(Column("status", "Status", 110));
        _queue.Columns.Add(FillColumn("phase", "Etap", 26));
        _queue.Columns.Add(Column("processed", "Postęp", 120));
        _queue.Columns.Add(Column("errors", "Błędy", 70));
    }

    static DataGridViewTextBoxColumn Column(
        string name,
        string header,
        int width)
        => new()
        {
            Name = name,
            HeaderText = header,
            Width = width,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

    static DataGridViewTextBoxColumn FillColumn(
        string name,
        string header,
        float weight)
        => new()
        {
            Name = name,
            HeaderText = header,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = weight,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        };

    static IReadOnlyDictionary<ProcessingStageKind, Label> CreateStageLabels()
        => Enum.GetValues<ProcessingStageKind>()
            .Where(value => value != ProcessingStageKind.Other)
            .ToDictionary(
                value => value,
                value => MailboxUi.Label(
                    $"{StageName(value)}  0 / 0",
                    8.5f,
                    FontStyle.Bold,
                    MailboxUi.Muted));

    async Task RefreshDashboardAsync(bool quiet = false)
    {
        if (_refreshing)
            return;
        string? key = _keyProvider();
        if (key is null)
        {
            _snapshot = null;
            _mailboxes.Rows.Clear();
            _queue.Rows.Clear();
            _status.Text = "Sesja jest zablokowana.";
            UpdateMetrics(null);
            UpdateActions();
            return;
        }

        _refreshing = true;
        long[] selected = SelectedSourceIds();
        try
        {
            MailboxDashboardSnapshot snapshot = await Task.Run(() =>
            {
                using var connection = Db.Open(key, create: false);
                Db.EnsureSchema(connection);
                return MailboxDashboard.Read(connection);
            });
            if (IsDisposed || Disposing)
                return;
            _snapshot = snapshot;
            RenderSources(snapshot, selected);
            RenderQueue(snapshot);
            UpdateMetrics(snapshot);
            UpdateStages(snapshot.Processing);
            UpdateActions();
            if (!quiet && _pipelineTask is null)
                _status.Text = snapshot.ActiveRun is null
                    ? "Gotowe. Wybierz skrzynki i uruchom import."
                    : $"Odzyskano aktywny przebieg #{snapshot.ActiveRun.Id}.";
        }
        catch (Exception exception)
        {
            if (!quiet && !IsDisposed)
                _status.Text = $"Nie można odświeżyć: {SafeError(exception)}";
        }
        finally
        {
            _refreshing = false;
        }
    }

    void RenderSources(
        MailboxDashboardSnapshot snapshot,
        IReadOnlyCollection<long> selected)
    {
        _mailboxes.SuspendLayout();
        try
        {
            _mailboxes.Rows.Clear();
            foreach (MailboxDashboardSource item in snapshot.Sources)
            {
                MailboxImportSourceRecord? latest = item.LatestImport;
                int rowIndex = _mailboxes.Rows.Add(
                    ProviderName(item.Source.Provider),
                    item.Source.DisplayName,
                    SourceIdentity(item.Source),
                    item.Source.Enabled ? "Aktywna" : "Wyłączona",
                    item.MessageCount.ToString("N0"),
                    latest is null
                        ? "Jeszcze nie importowano"
                        : $"{SourceStatus(latest.Status)} · {latest.Processed:N0}",
                    latest?.Errors.ToString("N0") ?? "0");
                DataGridViewRow row = _mailboxes.Rows[rowIndex];
                row.Tag = item.Source.Id;
                if (!item.Source.Enabled)
                    row.DefaultCellStyle.ForeColor = MailboxUi.Muted;
                if (latest?.Status == MailboxImportSourceStatus.Failed)
                    row.Cells["last"].Style.ForeColor = MailboxUi.Danger;
                if (selected.Contains(item.Source.Id))
                    row.Selected = true;
            }
        }
        finally
        {
            _mailboxes.ResumeLayout();
        }
        _sectionTitle.Text = $"Skonfigurowane skrzynki ({snapshot.Sources.Count})";
    }

    void RenderQueue(MailboxDashboardSnapshot snapshot)
    {
        _queue.Rows.Clear();
        foreach (MailboxImportSourceRecord source in snapshot.ActiveRunSources)
        {
            _queue.Rows.Add(
                source.QueuePosition + 1,
                ProviderName(source.Provider),
                source.DisplayName,
                SourceStatus(source.Status),
                PhaseName(source.Phase),
                source.Total is > 0
                    ? $"{source.Processed:N0} / {source.Total:N0}"
                    : source.Processed.ToString("N0"),
                source.Errors.ToString("N0"));
        }
    }

    void UpdateMetrics(MailboxDashboardSnapshot? snapshot)
    {
        _configuredMetric.Text = snapshot is null
            ? "—"
            : $"{snapshot.EnabledSources:N0} / {snapshot.Sources.Count:N0}";
        _messagesMetric.Text = snapshot?.MessageCount.ToString("N0") ?? "—";
        _runMetric.Text = snapshot?.ActiveRun is { } active
            ? $"#{active.Id} · {RunStatus(active.Status)}"
            : "Brak";
        _processingMetric.Text = snapshot?.Processing is { } pipeline
            ? $"{pipeline.Completed + pipeline.Failed:N0} / {pipeline.Total:N0}"
            : "0 / 0";
    }

    void UpdateStages(ProcessingPipelineSnapshot? pipeline)
    {
        foreach ((ProcessingStageKind kind, Label label) in _stageLabels)
        {
            ProcessingStageSnapshot? stage =
                pipeline?.Stages.FirstOrDefault(item => item.Stage == kind);
            long finished = (stage?.Completed ?? 0) + (stage?.Failed ?? 0);
            label.Text = $"{StageName(kind)}  {finished:N0} / {stage?.Total ?? 0:N0}";
            label.ForeColor = stage?.Failed > 0
                ? MailboxUi.Danger
                : stage?.Running > 0
                    ? MailboxUi.Primary
                    : stage?.Total > 0 && finished == stage.Total
                        ? MailboxUi.Success
                        : MailboxUi.Muted;
        }

        if (pipeline is null || pipeline.Total == 0)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = 0;
            return;
        }
        long finishedTotal = pipeline.Completed + pipeline.Failed;
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = (int)Math.Clamp(
            finishedTotal * _progress.Maximum / pipeline.Total,
            0,
            _progress.Maximum);
    }

    async Task AddGmailAsync()
    {
        await RunAddAsync(async (key, cancellationToken) =>
        {
            using var connection = Db.Open(key, create: false);
            Db.EnsureSchema(connection);
            GmailAccountRecord account = await GmailOAuth.ConnectAsync(
                connection,
                key,
                cancellationToken);
            MailboxSourceRecord source =
                MailboxSourceRepository.Find(
                    connection,
                    MailboxProvider.Gmail,
                    account.Email)
                ?? throw new InvalidOperationException(
                    "Nie zarejestrowano źródła Gmail.");
            return (source, $"Połączono Gmail: {account.Email}.");
        });
    }

    async Task AddImapAsync()
    {
        string? key = _keyProvider();
        if (key is null)
        {
            _status.Text = "Najpierw odblokuj sesję.";
            return;
        }
        using var form = new ImapAccountForm(key);
        if (form.ShowDialog(this) != DialogResult.OK
            || form.Configuration is not { } configuration)
            return;

        await RunAddAsync((sessionKey, _) => Task.Run(() =>
        {
            using var connection = Db.Open(sessionKey, create: false);
            Db.EnsureSchema(connection);
            string externalKey =
                ImapMailboxRegistration.ExternalKey(configuration.Account);
            if (MailboxSourceRepository.Find(
                    connection,
                    MailboxProvider.Imap,
                    externalKey) is not null)
                throw new InvalidOperationException(
                    "Ta skrzynka IMAP jest już skonfigurowana.");

            ImapAccounts accounts = ImapAccounts.Load();
            if (accounts.Find(configuration.Account.Name) is not null)
                throw new InvalidOperationException(
                    "Konto IMAP o tej nazwie już istnieje.");
            accounts.Accounts.Add(configuration.Account);
            accounts.Save();
            try
            {
                MailboxSourceRecord source = ImapMailboxRegistration.Register(
                    connection,
                    configuration.Account,
                    configuration.MaxPerFolder,
                    configuration.SinceUtc);
                return (
                    source,
                    $"Dodano IMAP: {configuration.Account.Name}.");
            }
            catch
            {
                accounts.Accounts.Remove(configuration.Account);
                accounts.Save();
                throw;
            }
        }));
    }

    async Task AddOutlookAsync()
    {
        if (_keyProvider() is null)
        {
            _status.Text = "Najpierw odblokuj sesję.";
            return;
        }
        using var form = new OutlookStoreForm();
        if (form.ShowDialog(this) != DialogResult.OK
            || form.Configuration is not { } configuration)
            return;

        await RunAddAsync((key, _) => Task.Run(() =>
        {
            using var connection = Db.Open(key, create: false);
            Db.EnsureSchema(connection);
            MailboxSourceRecord source = OutlookMailboxRegistration.Register(
                connection,
                configuration.Store,
                configuration.MaxPerFolder,
                sinceUtc: configuration.SinceUtc);
            return (
                source,
                $"Dodano Outlook: {configuration.Store.DisplayName}.");
        }));
    }

    async Task RunAddAsync(
        Func<string, CancellationToken, Task<(MailboxSourceRecord Source, string Message)>> action)
    {
        if (_adding)
            return;
        string? key = _keyProvider();
        if (key is null)
        {
            _status.Text = "Najpierw odblokuj sesję.";
            return;
        }

        _adding = true;
        _addCancellation = new CancellationTokenSource();
        _add.Enabled = false;
        _status.Text = "Dodawanie skrzynki…";
        try
        {
            (MailboxSourceRecord source, string message) =
                await action(key, _addCancellation.Token);
            bool appended = await AppendToRunningImportAsync(key, source.Id);
            _status.Text = appended
                ? $"{message} Dodano ją także do bieżącej kolejki."
                : message;
            await RefreshDashboardAsync(quiet: true);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Dodawanie skrzynki anulowane.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Nie dodano skrzynki: {SafeError(exception)}";
        }
        finally
        {
            _addCancellation.Dispose();
            _addCancellation = null;
            _adding = false;
            UpdateActions();
        }
    }

    async Task<bool> AppendToRunningImportAsync(
        string key,
        long sourceId)
        => await Task.Run(() =>
        {
            using var connection = Db.Open(key, create: false);
            Db.EnsureSchema(connection);
            MailboxImportRunRecord? active =
                MailboxImportRunRepository.FindActive(connection);
            if (active is null
                || active.Status is not (
                    MailboxImportRunStatus.Queued
                    or MailboxImportRunStatus.Importing))
                return false;
            if (MailboxImportRunRepository.ListSources(connection, active.Id)
                .Any(item => item.MailboxSourceId == sourceId))
                return false;
            MailboxImportRunRepository.AppendSource(
                connection,
                active.Id,
                sourceId);
            return true;
        });

    async Task QueueSourcesAsync(long[] sourceIds)
    {
        string? key = _keyProvider();
        if (key is null)
        {
            _status.Text = "Najpierw odblokuj sesję.";
            return;
        }
        if (sourceIds.Length == 0)
        {
            _status.Text = "Wybierz co najmniej jedną aktywną skrzynkę.";
            return;
        }

        bool forceFull = _forceFull.Checked;
        try
        {
            (long RunId, int Added) queued = await Task.Run(() =>
            {
                using var connection = Db.Open(key, create: false);
                Db.EnsureSchema(connection);
                MailboxSourceRecord[] selected = sourceIds
                    .Distinct()
                    .Select(id => MailboxSourceRepository.Find(connection, id)
                        ?? throw new InvalidOperationException(
                            "Wybrana skrzynka już nie istnieje."))
                    .Where(source => source.Enabled)
                    .ToArray();
                if (selected.Length == 0)
                    throw new InvalidOperationException(
                        "Wybrane skrzynki są wyłączone.");

                MailboxImportRunRecord? active =
                    MailboxImportRunRepository.FindActive(connection);
                if (active is null)
                {
                    MailboxImportRunRecord created =
                        MailboxImportRunRepository.Create(
                            connection,
                            selected.Select(source => source.Id),
                            forceFull);
                    return (created.Id, selected.Length);
                }
                if (active.Status is not (
                    MailboxImportRunStatus.Queued
                    or MailboxImportRunStatus.Importing))
                    throw new InvalidOperationException(
                        "Trwa już indeksowanie. Nowe skrzynki uruchom w następnym przebiegu.");

                HashSet<long> existing = MailboxImportRunRepository
                    .ListSources(connection, active.Id)
                    .Where(item => item.MailboxSourceId.HasValue)
                    .Select(item => item.MailboxSourceId!.Value)
                    .ToHashSet();
                int added = 0;
                foreach (MailboxSourceRecord source in selected)
                {
                    if (existing.Add(source.Id))
                    {
                        MailboxImportRunRepository.AppendSource(
                            connection,
                            active.Id,
                            source.Id);
                        added++;
                    }
                }
                return (active.Id, added);
            });
            _status.Text = queued.Added == 0
                ? "Wybrane skrzynki są już w kolejce."
                : $"Dodano do kolejki: {queued.Added}.";
            await RefreshDashboardAsync(quiet: true);
            await EnsurePipelineRunningAsync(queued.RunId);
        }
        catch (Exception exception)
        {
            _status.Text = $"Nie uruchomiono importu: {SafeError(exception)}";
        }
    }

    async Task EnsurePipelineRunningAsync(long runId)
    {
        if (_pipelineTask is not null)
            return;
        string? key = _keyProvider();
        if (key is null)
            return;

        _pipelineCancellation = new CancellationTokenSource();
        var progress = new Progress<MailboxPipelineUpdate>(ApplyPipelineUpdate);
        Task execution = RunPipelineAsync(
            key,
            runId,
            progress,
            _pipelineCancellation.Token);
        _pipelineTask = execution;
        UpdateActions();
        try
        {
            await execution;
        }
        finally
        {
            _pipelineCancellation.Dispose();
            _pipelineCancellation = null;
            _pipelineTask = null;
            _sessionCancellationReported = false;
            await RefreshDashboardAsync(quiet: true);
            UpdateActions();
        }
    }

    async Task RunPipelineAsync(
        string key,
        long runId,
        IProgress<MailboxPipelineUpdate> progress,
        CancellationToken cancellationToken)
    {
        try
        {
            MailboxPipelineResult result = await _pipeline.RunAsync(
                key,
                runId,
                forceFull: null,
                progress,
                cancellationToken);
            if (IsDisposed)
                return;
            _status.Text = result.Run.Status switch
            {
                MailboxImportRunStatus.Completed =>
                    "Import i przetwarzanie zakończone.",
                MailboxImportRunStatus.CompletedWithErrors =>
                    "Przebieg zakończony z błędami. Szczegóły są widoczne w kolejce.",
                MailboxImportRunStatus.Cancelled =>
                    "Kolejka została anulowana.",
                MailboxImportRunStatus.Failed =>
                    $"Kolejka nie powiodła się ({result.Run.LastErrorCode ?? "brak kodu"}).",
                _ => $"Przebieg pozostaje w stanie: {RunStatus(result.Run.Status)}.",
            };
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                _status.Text = "Anulowanie kolejki…";
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _status.Text = $"Błąd kolejki: {SafeError(exception)}";
        }
    }

    void ApplyPipelineUpdate(MailboxPipelineUpdate update)
    {
        if (IsDisposed)
            return;
        if (update.Import is { } importing)
        {
            _status.Text = importing.Progress is { } value
                ? $"{PhaseName(value.Phase)} · {value.Processed:N0}"
                    + (value.Total is > 0 ? $" / {value.Total:N0}" : "")
                : $"Import skrzynek · {RunStatus(importing.RunStatus)}";
            if (importing.Progress is { Total: > 0 } known)
                SetProgress(known.Processed, known.Total.Value);
            else
                SetIndeterminateProgress();
        }
        else if (update.Processing is { } processing)
        {
            UpdateStages(processing.Pipeline);
            _status.Text = ProcessingText(processing.Pipeline);
        }
    }

    async Task ToggleSelectedAsync()
    {
        long[] ids = SelectedSourceIds();
        if (ids.Length != 1)
        {
            _status.Text = "Wybierz jedną skrzynkę do włączenia lub wyłączenia.";
            return;
        }
        string? key = _keyProvider();
        if (key is null)
            return;
        try
        {
            await Task.Run(() =>
            {
                using var connection = Db.Open(key, create: false);
                Db.EnsureSchema(connection);
                MailboxSourceRecord source =
                    MailboxSourceRepository.Find(connection, ids[0])
                    ?? throw new InvalidOperationException(
                        "Skrzynka już nie istnieje.");
                MailboxSourceRepository.SetEnabled(
                    connection,
                    source.Id,
                    !source.Enabled);
            });
            await RefreshDashboardAsync(quiet: true);
            _status.Text = "Zmieniono dostępność skrzynki.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Nie zmieniono skrzynki: {SafeError(exception)}";
        }
    }

    void CancelPipeline()
    {
        if (_pipelineCancellation is null)
            return;
        _status.Text = "Anulowanie kolejki…";
        _pipelineCancellation.Cancel();
        _cancel.Enabled = false;
    }

    void CheckSession()
    {
        if (_pipelineTask is null || _keyProvider() is not null)
            return;
        _pipelineCancellation?.Cancel();
        if (!_sessionCancellationReported)
        {
            _sessionCancellationReported = true;
            _status.Text =
                "Sesja została zablokowana — bezpiecznie zatrzymuję kolejkę.";
        }
    }

    void UpdateActions()
    {
        bool unlocked = _keyProvider() is not null;
        bool hasSelection = SelectedSourceIds().Length > 0;
        bool active = _snapshot?.ActiveRun is not null;
        bool canAppend = _snapshot?.ActiveRun?.Status is
            MailboxImportRunStatus.Queued
            or MailboxImportRunStatus.Importing;

        _add.Enabled = unlocked && !_adding;
        _refresh.Enabled = unlocked && !_refreshing;
        _toggle.Enabled = unlocked
            && SelectedSourceIds().Length == 1
            && !active;
        _importSelected.Enabled = unlocked
            && hasSelection
            && (!active || canAppend);
        _importAll.Enabled = unlocked
            && (_snapshot?.EnabledSources ?? 0) > 0
            && (!active || canAppend);
        _importSelected.Text = canAppend
            ? "Dodaj wybrane do kolejki"
            : "Importuj wybrane";
        _importAll.Text = canAppend
            ? "Dodaj wszystkie do kolejki"
            : "Importuj wszystkie";
        _forceFull.Enabled = unlocked && !active;
        _cancel.Enabled = unlocked && active && _pipelineCancellation is not null;
    }

    long[] SelectedSourceIds()
        => _mailboxes.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<long>()
            .Distinct()
            .ToArray();

    void SetProgress(long completed, long total)
    {
        if (total <= 0)
        {
            SetIndeterminateProgress();
            return;
        }
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = (int)Math.Clamp(
            completed * _progress.Maximum / total,
            0,
            _progress.Maximum);
    }

    void SetIndeterminateProgress()
    {
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 24;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_adding)
        {
            e.Cancel = true;
            _addCancellation?.Cancel();
            _status.Text =
                "Anuluję dodawanie skrzynki. Zamknij okno po zakończeniu operacji.";
            return;
        }
        if (_pipelineTask is not null)
        {
            e.Cancel = true;
            CancelPipeline();
            _status.Text =
                "Zatrzymuję aktywną kolejkę. Zamknij okno po zakończeniu anulowania.";
            return;
        }
        _timer.Stop();
        _pipeline.Dispose();
        base.OnFormClosing(e);
    }

    static string ProviderName(MailboxProvider provider) => provider switch
    {
        MailboxProvider.Gmail => "Gmail",
        MailboxProvider.Imap => "IMAP",
        MailboxProvider.Outlook => "Outlook",
        _ => provider.ToString(),
    };

    static string SourceIdentity(MailboxSourceRecord source)
    {
        if (source.Provider == MailboxProvider.Gmail)
            return source.ExternalKey;
        if (source.Provider == MailboxProvider.Imap)
        {
            try
            {
                ImapMailboxSettings settings =
                    ImapMailboxRegistration.ReadSettings(source.SettingsJson);
                return $"{settings.User} · {settings.Host}:{settings.Port}";
            }
            catch
            {
                return source.DisplayName;
            }
        }
        try
        {
            OutlookMailboxSettings settings =
                OutlookMailboxRegistration.ReadSettings(source.SettingsJson);
            return settings.FilePath is { Length: > 0 }
                ? Path.GetFileName(settings.FilePath)
                : "Skrzynka Outlook";
        }
        catch
        {
            return source.DisplayName;
        }
    }

    static string RunStatus(MailboxImportRunStatus status) => status switch
    {
        MailboxImportRunStatus.Queued => "oczekuje",
        MailboxImportRunStatus.Importing => "import",
        MailboxImportRunStatus.Processing => "indeksowanie",
        MailboxImportRunStatus.Completed => "gotowe",
        MailboxImportRunStatus.CompletedWithErrors => "gotowe z błędami",
        MailboxImportRunStatus.Cancelled => "anulowano",
        MailboxImportRunStatus.Failed => "błąd",
        _ => status.ToString(),
    };

    static string SourceStatus(MailboxImportSourceStatus status) => status switch
    {
        MailboxImportSourceStatus.Queued => "Oczekuje",
        MailboxImportSourceStatus.Importing => "Import",
        MailboxImportSourceStatus.Imported => "Zaimportowano",
        MailboxImportSourceStatus.Failed => "Błąd",
        MailboxImportSourceStatus.Cancelled => "Anulowano",
        _ => status.ToString(),
    };

    static string StageName(ProcessingStageKind stage) => stage switch
    {
        ProcessingStageKind.Download => "Pobieranie",
        ProcessingStageKind.Extract => "Ekstrakcja",
        ProcessingStageKind.Ocr => "OCR",
        ProcessingStageKind.Transcribe => "Transkrypcja",
        ProcessingStageKind.Embed => "Indeks",
        _ => "Inne",
    };

    static string PhaseName(string phase) => phase switch
    {
        "queued" => "Oczekuje",
        "connecting" => "Łączenie",
        "listing-folders" => "Odczyt folderów",
        "importing" => "Importowanie",
        "importing-full" => "Pełny import",
        "importing-incremental" => "Import przyrostowy",
        "retrying" => "Ponawianie",
        "resetting-history" => "Odbudowa historii",
        "imported" => "Zaimportowano",
        "failed" => "Błąd importu",
        "cancelled" => "Anulowano",
        _ => phase.Replace('-', ' '),
    };

    static string ProcessingText(ProcessingPipelineSnapshot pipeline)
    {
        if (pipeline.Total == 0)
            return "Brak załączników wymagających przetwarzania.";
        ProcessingStageSnapshot? running = pipeline.Stages.FirstOrDefault(
            stage => stage.Running > 0);
        if (running is not null)
            return $"{StageName(running.Stage)} · aktywne {running.Running:N0}, "
                + $"zakończone {pipeline.Completed:N0} / {pipeline.Total:N0}";
        if (pipeline.Pending > 0)
            return $"Kolejka przetwarzania · oczekuje {pipeline.Pending:N0}, "
                + $"zakończone {pipeline.Completed:N0} / {pipeline.Total:N0}";
        return $"Przetwarzanie zakończone · {pipeline.Completed:N0} / {pipeline.Total:N0}";
    }

    static string SafeError(Exception exception) => exception switch
    {
        GmailAuthorizationException
            or InvalidOperationException
            or InvalidDataException
            or FileNotFoundException => exception.Message,
        _ => exception.GetType().Name,
    };
}

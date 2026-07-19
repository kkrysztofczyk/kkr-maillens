using System.Diagnostics;
using KKR.MailLens;

namespace KKR.MailLens.Gui;

sealed class GmailManagerForm : Form
{
    sealed record DashboardRow(long Id, string Email, long Messages, string LastSync,
        string InitialSync, long Errors, string Operation);

    readonly Func<string?> _keyProvider;
    readonly ListView _accounts = new()
    {
        Dock = DockStyle.Fill,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        MultiSelect = false,
        View = View.Details,
    };
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Style = ProgressBarStyle.Marquee, Visible = false };
    readonly TextBox _log = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9),
    };
    readonly Button _connect = Button("Połącz konto");
    readonly Button _sync = Button("Synchronizuj");
    readonly Button _fullSync = Button("Pełna synchronizacja");
    readonly Button _cancel = Button("Anuluj");
    readonly Button _disconnect = Button("Odłącz");
    readonly Button _worker = Button("Uruchom Worker");
    readonly Button _retry = Button("Ponów błędy");
    readonly Button _refresh = Button("Odśwież");
    readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 1000 };
    CancellationTokenSource? _operation;
    bool _gmailOperation;

    public GmailManagerForm(Func<string?> keyProvider)
    {
        _keyProvider = keyProvider;
        Text = "KKR MailLens — Gmail i załączniki";
        Width = 1050;
        Height = 620;
        MinimumSize = new Size(850, 480);
        StartPosition = FormStartPosition.CenterParent;

        _accounts.Columns.Add("ID", 50);
        _accounts.Columns.Add("Konto", 230);
        _accounts.Columns.Add("Wiadomości", 90);
        _accounts.Columns.Add("Ostatnia synchronizacja", 185);
        _accounts.Columns.Add("Pierwszy import", 110);
        _accounts.Columns.Add("Błędy", 70);
        _accounts.Columns.Add("Operacja", 160);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        buttons.Controls.AddRange([_connect, _sync, _fullSync, _cancel, _disconnect, _worker, _retry, _refresh]);
        root.Controls.Add(buttons, 0, 0);
        root.Controls.Add(_accounts, 0, 1);
        root.Controls.Add(_status, 0, 2);
        root.Controls.Add(_progress, 0, 3);
        root.Controls.Add(_log, 0, 4);
        Controls.Add(root);

        _connect.Click += async (_, _) => await ConnectAsync();
        _sync.Click += async (_, _) => await SyncAsync(full: false);
        _fullSync.Click += async (_, _) => await SyncAsync(full: true);
        _cancel.Click += (_, _) => CancelOperation();
        _disconnect.Click += async (_, _) => await DisconnectAsync();
        _worker.Click += async (_, _) => await RunWorkerAsync();
        _retry.Click += async (_, _) => await RetryFailedAsync();
        _refresh.Click += async (_, _) => await RefreshDashboardAsync();
        Shown += async (_, _) => await RefreshDashboardAsync();
        _sessionTimer.Tick += (_, _) => CheckSession();
        _sessionTimer.Start();
        SetBusy(false);
    }

    static Button Button(string text) => new() { Text = text, AutoSize = true, Height = 29, Margin = new Padding(3) };

    long? SelectedAccountId => _accounts.SelectedItems.Count == 1
        && _accounts.SelectedItems[0].Tag is long id ? id : null;

    async Task RefreshDashboardAsync()
    {
        string? key = _keyProvider();
        if (key is null)
        {
            _accounts.Items.Clear();
            _status.Text = "Sesja jest zablokowana.";
            return;
        }

        long? selectedId = SelectedAccountId;
        try
        {
            (IReadOnlyList<DashboardRow> rows, IReadOnlyDictionary<string, long> jobs) = await Task.Run(() =>
            {
                using var connection = Db.Open(key, create: false);
                Db.EnsureSchema(connection);
                IReadOnlyList<DashboardRow> dashboard = GmailRepository.ListAccounts(connection)
                    .Select(account => new DashboardRow(account.Id, account.Email,
                        GmailRepository.MessageCount(connection, account.Id), account.LastSyncAt ?? "—",
                        account.InitialSyncCompleted ? "zakończony" : "oczekuje",
                        GmailRepository.ErrorCount(connection, account.Id), account.CurrentOperation ?? "bezczynna"))
                    .ToArray();
                return (dashboard, ProcessingJobRepository.Counts(connection));
            });

            // Odswiezanie nie jest sledzone w _operation, wiec OnFormClosing go nie blokuje - okno moglo
            // zostac zamkniete i zwolnione w trakcie await; dotkniecie kontrolek rzuciloby ObjectDisposedException.
            if (IsDisposed || Disposing) return;

            _accounts.BeginUpdate();
            try
            {
                _accounts.Items.Clear();
                foreach (DashboardRow row in rows)
                {
                    var item = new ListViewItem(row.Id.ToString()) { Tag = row.Id };
                    item.SubItems.Add(row.Email);
                    item.SubItems.Add(row.Messages.ToString());
                    item.SubItems.Add(row.LastSync);
                    item.SubItems.Add(row.InitialSync);
                    item.SubItems.Add(row.Errors.ToString());
                    item.SubItems.Add(row.Operation);
                    _accounts.Items.Add(item);
                    if (row.Id == selectedId) item.Selected = true;
                }
            }
            finally { _accounts.EndUpdate(); }

            _status.Text = $"Konta: {rows.Count} | kolejka: oczekujące={jobs.GetValueOrDefault("pending")}, "
                + $"działające={jobs.GetValueOrDefault("running")}, zakończone={jobs.GetValueOrDefault("completed")}, "
                + $"błędy={jobs.GetValueOrDefault("failed")}";
        }
        catch (Exception ex) { Log("Błąd odświeżania: " + SafeError(ex)); }
    }

    async Task ConnectAsync()
    {
        await RunOperation("Łączenie konta Gmail…", async (key, cancellationToken) =>
        {
            GmailAccountRecord account = await Task.Run(async () =>
            {
                using var connection = Db.Open(key, create: false);
                Db.EnsureSchema(connection);
                return await GmailOAuth.ConnectAsync(connection, key, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
            return $"Połączono konto {account.Email}.";
        });
    }

    async Task SyncAsync(bool full)
    {
        long? selectedId = SelectedAccountId;
        var progress = new Progress<GmailSyncProgress>(value =>
        {
            _status.Text = $"{value.Phase}: przetworzono={value.Processed}, błędy={value.Errors}";
        });

        await RunOperation(full ? "Pełna synchronizacja Gmail…" : "Synchronizacja Gmail…",
            async (key, cancellationToken) => await Task.Run(async () =>
            {
                GmailCancellation.Clear();
                try
                {
                    using var connection = Db.Open(key, create: false);
                    Db.EnsureSchema(connection);
                    IReadOnlyList<GmailAccountRecord> accounts = selectedId is null
                        ? GmailRepository.ListAccounts(connection)
                        : GmailRepository.FindAccount(connection, selectedId.Value) is { } selected
                            ? [selected] : [];
                    if (accounts.Count == 0) throw new InvalidOperationException("Brak konta do synchronizacji.");

                    long processed = 0, inserted = 0, updated = 0, deleted = 0, errors = 0;
                    foreach (GmailAccountRecord account in accounts)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using IGmailApiClient api = await GmailOAuth.CreateApiClientAsync(account, key, cancellationToken)
                            .ConfigureAwait(false);
                        var synchronizer = new GmailSynchronizer(connection, api, progress);
                        GmailSyncResult result = await synchronizer.SyncAsync(account, full, cancellationToken)
                            .ConfigureAwait(false);
                        processed += result.Processed;
                        inserted += result.Inserted;
                        updated += result.Updated;
                        deleted += result.Deleted;
                        errors += result.Errors;
                    }
                    return $"Synchronizacja zakończona: przetworzono={processed}, nowe={inserted}, "
                        + $"aktualizacje={updated}, usunięte={deleted}, błędy={errors}.";
                }
                finally { GmailCancellation.Clear(); }
            }, cancellationToken), gmailOperation: true);
    }

    async Task DisconnectAsync()
    {
        long? accountId = SelectedAccountId;
        if (accountId is null) { Log("Wybierz konto do odłączenia."); return; }
        if (MessageBox.Show(this, "Odłączyć konto i usunąć jego lokalne wiadomości oraz token OAuth?",
            "Odłącz konto", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

        await RunOperation("Odłączanie konta…", async (key, cancellationToken) => await Task.Run(async () =>
        {
            using var connection = Db.Open(key, create: false);
            Db.EnsureSchema(connection);
            GmailAccountRecord account = GmailRepository.FindAccount(connection, accountId.Value)
                ?? throw new InvalidOperationException("Konto już nie istnieje.");
            await GmailOAuth.RemoveTokenAsync(account.TokenKey, key).ConfigureAwait(false);
            GmailRepository.DeleteAccount(connection, account.Id);
            return $"Odłączono konto {account.Email}.";
        }, cancellationToken));
    }

    async Task RetryFailedAsync()
    {
        await RunOperation("Przywracanie zadań…", (key, cancellationToken) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Db.Open(key, create: false);
            Db.EnsureSchema(connection);
            return $"Przywrócono do kolejki: {ProcessingJobRepository.RetryFailed(connection)}.";
        }, cancellationToken));
    }

    async Task RunWorkerAsync()
    {
        await RunOperation("Worker przetwarza kolejkę…", async (_, cancellationToken) =>
        {
            string executable = Path.Combine(AppContext.BaseDirectory, "KKR.MailLens.Worker.exe");
            if (!File.Exists(executable))
                throw new FileNotFoundException("Brak KKR.MailLens.Worker.exe w katalogu aplikacji.", executable);

            int memoryLimitMb = Math.Clamp(AppConfig.Load().WorkerMemoryLimitMb, 256, 16_384);
            using RestrictedWorkerProcess worker = RestrictedWorkerProcess.Start(executable, "--drain",
                memoryLimitMb * 1024L * 1024L);
            await worker.Process.WaitForExitAsync(cancellationToken);
            return $"Worker zakończył działanie z kodem {worker.Process.ExitCode}.";
        });
    }

    async Task RunOperation(string status, Func<string, CancellationToken, Task<string>> action,
        bool gmailOperation = false)
    {
        if (_operation is not null) { Log("Inna operacja nadal trwa."); return; }
        string? key = _keyProvider();
        if (key is null) { Log("Najpierw odblokuj sesję."); return; }

        _operation = new CancellationTokenSource();
        _gmailOperation = gmailOperation;
        SetBusy(true);
        _status.Text = status;
        try
        {
            string result = await action(key, _operation.Token);
            Log(result);
        }
        catch (OperationCanceledException) { Log("Operacja anulowana."); }
        catch (Exception ex) { Log("Błąd: " + SafeError(ex)); }
        finally
        {
            _operation.Dispose();
            _operation = null;
            _gmailOperation = false;
            if (!IsDisposed && !Disposing) // zamkniecie inne niz UserClosing (np. wyjscie aplikacji) nie czeka na operacje
            {
                SetBusy(false);
                await RefreshDashboardAsync();
            }
        }
    }

    void CancelOperation()
    {
        if (_gmailOperation) GmailCancellation.Request();
        _operation?.Cancel();
        _status.Text = "Anulowanie operacji…";
    }

    void CheckSession()
    {
        if (_operation is not null && _keyProvider() is null)
        {
            _operation.Cancel();
            _status.Text = "Sesja została zablokowana — anulowanie operacji…";
        }
    }

    void SetBusy(bool busy)
    {
        _connect.Enabled = _sync.Enabled = _fullSync.Enabled = _disconnect.Enabled = _worker.Enabled
            = _retry.Enabled = _refresh.Enabled = _accounts.Enabled = !busy;
        _cancel.Enabled = busy;
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }

    void Log(string message)
    {
        // Kontynuacje async moga dobiec juz po zamknieciu okna - log do zwolnionej kontrolki by rzucil.
        if (IsDisposed || Disposing) return;
        _log.AppendText((_log.TextLength == 0 ? "" : Environment.NewLine) + message);
    }

    static string SafeError(Exception exception) => exception switch
    {
        GmailAuthorizationException or FileNotFoundException or InvalidOperationException => exception.Message,
        _ => exception.GetType().Name,
    };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_operation is not null)
        {
            e.Cancel = true;
            CancelOperation();
            Log("Najpierw kończę aktywną operację. Zamknij okno ponownie po jej anulowaniu.");
            return;
        }
        _sessionTimer.Stop();
        base.OnFormClosing(e);
    }
}

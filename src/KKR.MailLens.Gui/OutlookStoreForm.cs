using KKR.MailLens;

namespace KKR.MailLens.Gui;

sealed record OutlookStoreConfiguration(
    OutlookStoreInfo Store,
    int MaxPerFolder,
    DateTime? SinceUtc);

sealed class OutlookStoreForm : Form
{
    readonly DataGridView _stores = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoGenerateColumns = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
    };
    readonly NumericUpDown _maximum = new()
    {
        Minimum = 0,
        Maximum = 1_000_000,
        Value = 5000,
        ThousandsSeparator = true,
        Width = 110,
    };
    readonly CheckBox _limitDate = new()
    {
        Text = "Importuj od",
        AutoSize = true,
    };
    readonly DateTimePicker _since = new()
    {
        Format = DateTimePickerFormat.Short,
        Value = DateTime.Today.AddDays(-30),
        Enabled = false,
        Width = 120,
    };
    readonly Label _status = MailboxUi.Label(
        "Wczytywanie magazynów Outlook…",
        9,
        FontStyle.Regular,
        MailboxUi.Muted);
    readonly Button _save = MailboxUi.PrimaryButton("Dodaj magazyn");
    IReadOnlyList<OutlookStoreInfo> _loaded = [];
    bool _loading;

    public OutlookStoreForm()
    {
        Text = "Dodaj magazyn Outlook";
        BackColor = MailboxUi.Canvas;
        ForeColor = MailboxUi.Text;
        Font = new Font("Segoe UI", 9);
        Width = 800;
        Height = 570;
        MinimumSize = new Size(700, 480);
        StartPosition = FormStartPosition.CenterParent;

        MailboxUi.StyleGrid(_stores);
        _stores.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "name",
            HeaderText = "Magazyn",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 35,
        });
        _stores.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "kind",
            HeaderText = "Typ",
            Width = 100,
        });
        _stores.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "path",
            HeaderText = "Plik / lokalizacja",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 65,
        });

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24),
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 18),
        };
        header.Controls.Add(MailboxUi.Label(
            "Magazyny podłączone w Outlook",
            19,
            FontStyle.Bold));
        header.Controls.Add(MailboxUi.Label(
            "KKR MailLens tylko odczytuje istniejące skrzynki oraz podmontowane pliki PST/OST.",
            9,
            FontStyle.Regular,
            MailboxUi.Muted));
        root.Controls.Add(header, 0, 0);

        Panel card = MailboxUi.Card();
        card.Dock = DockStyle.Fill;
        card.Controls.Add(_stores);
        root.Controls.Add(card, 0, 1);

        var options = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0),
        };
        options.Controls.Add(MailboxUi.Label(
            "Limit / folder",
            9,
            FontStyle.Bold,
            MailboxUi.Muted));
        options.Controls.Add(_maximum);
        options.Controls.Add(_limitDate);
        options.Controls.Add(_since);
        root.Controls.Add(options, 0, 2);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 16, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Anchor = AnchorStyles.Left;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
        };
        Button refresh = MailboxUi.SecondaryButton("Odśwież");
        Button browse = MailboxUi.SecondaryButton("Wskaż PST");
        Button cancel = MailboxUi.SecondaryButton("Anuluj");
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(refresh);
        actions.Controls.Add(browse);
        actions.Controls.Add(cancel);
        actions.Controls.Add(_save);
        footer.Controls.Add(_status, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);

        _limitDate.CheckedChanged += (_, _) => _since.Enabled = _limitDate.Checked;
        refresh.Click += async (_, _) => await LoadStoresAsync();
        browse.Click += (_, _) => SelectMountedPst();
        _save.Click += (_, _) => Save();
        _stores.CellDoubleClick += (_, _) => Save();
        Shown += async (_, _) => await LoadStoresAsync();
        AcceptButton = _save;
        CancelButton = cancel;
        _save.Enabled = false;
    }

    public OutlookStoreConfiguration? Configuration { get; private set; }

    async Task LoadStoresAsync()
    {
        if (_loading)
            return;
        _loading = true;
        _save.Enabled = false;
        _status.Text = "Wczytywanie magazynów Outlook…";
        UseWaitCursor = true;
        try
        {
            _loaded = await Task.Run(() =>
            {
                using var outlook = new Outlook();
                return outlook.ListStores();
            });
            if (IsDisposed)
                return;
            _stores.Rows.Clear();
            foreach (OutlookStoreInfo store in _loaded)
            {
                int index = _stores.Rows.Add(
                    store.DisplayName,
                    Kind(store.Kind),
                    store.FilePath ?? "Skrzynka Outlook");
                _stores.Rows[index].Tag = store;
            }
            _status.Text = _loaded.Count == 0
                ? "Outlook nie udostępnił żadnych magazynów."
                : $"Dostępne magazyny: {_loaded.Count}";
            _save.Enabled = _loaded.Count > 0;
            if (_stores.Rows.Count > 0)
                _stores.Rows[0].Selected = true;
        }
        catch (Exception exception)
        {
            if (!IsDisposed)
                _status.Text = $"Nie można odczytać Outlook: {SafeError(exception)}";
        }
        finally
        {
            _loading = false;
            UseWaitCursor = false;
        }
    }

    void SelectMountedPst()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Wskaż podmontowany plik PST",
            Filter = "Pliki danych Outlook (*.pst)|*.pst",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;

        OutlookStoreInfo? match = _loaded.FirstOrDefault(store =>
            store.FilePath is not null
            && Path.GetFullPath(store.FilePath).Equals(
                Path.GetFullPath(picker.FileName),
                StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _status.Text =
                "Ten plik PST nie jest podmontowany w Outlook. Podłącz go w Outlook i odśwież listę.";
            return;
        }

        foreach (DataGridViewRow row in _stores.Rows)
        {
            row.Selected = ReferenceEquals(row.Tag, match);
            if (row.Selected)
                _stores.CurrentCell = row.Cells[0];
        }
        _status.Text = $"Wybrano: {match.DisplayName}";
    }

    void Save()
    {
        if (_stores.SelectedRows.Count != 1
            || _stores.SelectedRows[0].Tag is not OutlookStoreInfo store)
        {
            _status.Text = "Wybierz magazyn z listy.";
            return;
        }
        Configuration = new OutlookStoreConfiguration(
            store,
            decimal.ToInt32(_maximum.Value),
            _limitDate.Checked ? _since.Value.Date.ToUniversalTime() : null);
        DialogResult = DialogResult.OK;
    }

    static string Kind(OutlookStoreKind kind) => kind switch
    {
        OutlookStoreKind.Mailbox => "Skrzynka",
        OutlookStoreKind.Pst => "PST",
        OutlookStoreKind.Ost => "OST",
        _ => "Plik danych",
    };

    static string SafeError(Exception exception)
        => exception is InvalidOperationException && exception.InnerException is not null
            ? exception.InnerException.Message
            : exception.Message;
}

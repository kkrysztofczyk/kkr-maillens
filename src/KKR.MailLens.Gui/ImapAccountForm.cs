using KKR.MailLens;

namespace KKR.MailLens.Gui;

sealed record ImapAccountConfiguration(
    ImapAccount Account,
    int MaxPerFolder,
    DateTime? SinceUtc);

sealed class ImapAccountForm : Form
{
    readonly string _sessionKeyHex;
    readonly TextBox _name = Field();
    readonly TextBox _host = Field();
    readonly NumericUpDown _port = new()
    {
        Minimum = 1,
        Maximum = 65535,
        Value = 993,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 10),
    };
    readonly ComboBox _security = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDownList,
        Font = new Font("Segoe UI", 10),
    };
    readonly TextBox _user = Field();
    readonly TextBox _password = Field(password: true);
    readonly NumericUpDown _maximum = new()
    {
        Minimum = 0,
        Maximum = 1_000_000,
        Value = 5000,
        ThousandsSeparator = true,
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 10),
    };
    readonly CheckBox _limitDate = new()
    {
        Text = "Importuj wiadomości od",
        AutoSize = true,
        ForeColor = MailboxUi.Text,
        Font = new Font("Segoe UI", 9),
    };
    readonly DateTimePicker _since = new()
    {
        Format = DateTimePickerFormat.Short,
        Value = DateTime.Today.AddDays(-30),
        Enabled = false,
        Width = 130,
        Font = new Font("Segoe UI", 9),
    };
    readonly Label _validation = MailboxUi.Label(
        "",
        9,
        FontStyle.Regular,
        MailboxUi.Danger);

    public ImapAccountForm(string sessionKeyHex)
    {
        _sessionKeyHex = sessionKeyHex;
        Text = "Dodaj skrzynkę IMAP";
        BackColor = MailboxUi.Canvas;
        ForeColor = MailboxUi.Text;
        Font = new Font("Segoe UI", 9);
        Width = 580;
        Height = 620;
        MinimumSize = new Size(540, 580);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        _security.Items.AddRange(["SSL/TLS", "STARTTLS"]);
        _security.SelectedIndex = 0;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(24),
            BackColor = MailboxUi.Canvas,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
            "Nowa skrzynka IMAP",
            19,
            FontStyle.Bold));
        header.Controls.Add(MailboxUi.Label(
            "Dane logowania są szyfrowane kluczem aktywnej sesji i ochroną Windows.",
            9,
            FontStyle.Regular,
            MailboxUi.Muted));
        root.Controls.Add(header, 0, 0);

        Panel card = MailboxUi.Card();
        card.Dock = DockStyle.Fill;
        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 8,
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int row = 0; row < 7; row++)
            form.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        form.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        AddRow(form, 0, "Nazwa", _name);
        AddRow(form, 1, "Serwer", _host);
        AddRow(form, 2, "Port", _port);
        AddRow(form, 3, "Zabezpieczenie", _security);
        AddRow(form, 4, "Użytkownik", _user);
        AddRow(form, 5, "Hasło aplikacji", _password);
        AddRow(form, 6, "Limit / folder", _maximum);

        var dateRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0),
        };
        dateRow.Controls.Add(_limitDate);
        dateRow.Controls.Add(_since);
        form.Controls.Add(dateRow, 0, 7);
        form.SetColumnSpan(dateRow, 2);
        card.Controls.Add(form);
        root.Controls.Add(card, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0, 16, 0, 0),
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _validation.Anchor = AnchorStyles.Left;
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
        };
        Button cancel = MailboxUi.SecondaryButton("Anuluj");
        Button save = MailboxUi.PrimaryButton("Dodaj skrzynkę");
        cancel.DialogResult = DialogResult.Cancel;
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        footer.Controls.Add(_validation, 0, 0);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        _security.SelectedIndexChanged += (_, _) =>
        {
            if (_security.SelectedIndex == 0 && _port.Value == 143)
                _port.Value = 993;
            else if (_security.SelectedIndex == 1 && _port.Value == 993)
                _port.Value = 143;
        };
        _limitDate.CheckedChanged += (_, _) => _since.Enabled = _limitDate.Checked;
        save.Click += (_, _) => Save();
        AcceptButton = save;
        CancelButton = cancel;
    }

    public ImapAccountConfiguration? Configuration { get; private set; }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _password.Clear();
        base.OnFormClosed(e);
    }

    void Save()
    {
        string name = _name.Text.Trim();
        string host = _host.Text.Trim();
        string user = _user.Text.Trim();
        if (name.Length == 0)
        {
            Invalid("Podaj nazwę skrzynki.", _name);
            return;
        }
        if (host.Length == 0)
        {
            Invalid("Podaj adres serwera IMAP.", _host);
            return;
        }
        if (user.Length == 0)
        {
            Invalid("Podaj nazwę użytkownika.", _user);
            return;
        }
        if (_password.TextLength == 0)
        {
            Invalid("Podaj hasło lub hasło aplikacji.", _password);
            return;
        }

        var account = new ImapAccount
        {
            Name = name,
            Host = host,
            Port = decimal.ToInt32(_port.Value),
            UseSsl = _security.SelectedIndex == 0,
            User = user,
        };
        try
        {
            account.SetPassword(_password.Text, _sessionKeyHex);
            Configuration = new ImapAccountConfiguration(
                account,
                decimal.ToInt32(_maximum.Value),
                _limitDate.Checked ? _since.Value.Date.ToUniversalTime() : null);
            DialogResult = DialogResult.OK;
        }
        catch (Exception exception)
        {
            _validation.Text = $"Nie można zabezpieczyć hasła: {exception.GetType().Name}";
        }
    }

    void Invalid(string message, Control control)
    {
        _validation.Text = message;
        control.Focus();
    }

    static TextBox Field(bool password = false)
        => new()
        {
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI", 10),
            UseSystemPasswordChar = password,
            BorderStyle = BorderStyle.FixedSingle,
        };

    static void AddRow(
        TableLayoutPanel form,
        int row,
        string label,
        Control control)
    {
        Label caption = MailboxUi.Label(
            label,
            9,
            FontStyle.Bold,
            MailboxUi.Muted);
        caption.Anchor = AnchorStyles.Left;
        control.Margin = new Padding(0, 8, 0, 8);
        form.Controls.Add(caption, 0, row);
        form.Controls.Add(control, 1, row);
    }
}

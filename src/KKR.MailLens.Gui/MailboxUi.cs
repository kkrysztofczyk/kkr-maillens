namespace KKR.MailLens.Gui;

static class MailboxUi
{
    public static readonly Color Canvas = Color.FromArgb(245, 247, 251);
    public static readonly Color Surface = Color.White;
    public static readonly Color Text = Color.FromArgb(15, 23, 42);
    public static readonly Color Muted = Color.FromArgb(100, 116, 139);
    public static readonly Color Border = Color.FromArgb(226, 232, 240);
    public static readonly Color Primary = Color.FromArgb(37, 99, 235);
    public static readonly Color PrimaryHover = Color.FromArgb(29, 78, 216);
    public static readonly Color Success = Color.FromArgb(22, 163, 74);
    public static readonly Color Warning = Color.FromArgb(217, 119, 6);
    public static readonly Color Danger = Color.FromArgb(220, 38, 38);
    public static readonly Color SoftBlue = Color.FromArgb(239, 246, 255);
    public static readonly Color SoftSlate = Color.FromArgb(248, 250, 252);

    public static Button PrimaryButton(string text) => Button(
        text,
        Primary,
        Color.White,
        PrimaryHover);

    public static Button SecondaryButton(string text) => Button(
        text,
        Surface,
        Text,
        SoftSlate);

    public static Button DangerButton(string text) => Button(
        text,
        Surface,
        Danger,
        Color.FromArgb(254, 242, 242));

    public static Panel Card()
        => new()
        {
            BackColor = Surface,
            Padding = new Padding(18),
            Margin = new Padding(0),
        };

    public static Label Label(
        string text,
        float size = 9,
        FontStyle style = FontStyle.Regular,
        Color? color = null)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = new Font("Segoe UI", size, style),
            ForeColor = color ?? Text,
            BackColor = Color.Transparent,
        };

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SoftSlate;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
        grid.ColumnHeadersDefaultCellStyle.Font =
            new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6);
        grid.ColumnHeadersHeight = 38;
        grid.RowHeadersVisible = false;
        grid.RowsDefaultCellStyle.BackColor = Surface;
        grid.RowsDefaultCellStyle.ForeColor = Text;
        grid.RowsDefaultCellStyle.SelectionBackColor = SoftBlue;
        grid.RowsDefaultCellStyle.SelectionForeColor = Text;
        grid.RowsDefaultCellStyle.Font = new Font("Segoe UI", 9);
        grid.RowTemplate.Height = 38;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
    }

    static Button Button(
        string text,
        Color background,
        Color foreground,
        Color hover)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(108, 36),
            Height = 36,
            BackColor = background,
            ForeColor = foreground,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(4, 0, 0, 0),
            Padding = new Padding(12, 0, 12, 0),
        };
        button.FlatAppearance.BorderColor = background == Surface ? Border : background;
        button.FlatAppearance.MouseOverBackColor = hover;
        button.FlatAppearance.MouseDownBackColor = hover;
        return button;
    }
}

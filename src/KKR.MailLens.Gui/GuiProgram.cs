using System.Windows.Forms;

namespace KKR.MailLens.Gui;

static class GuiProgram
{
    [STAThread]
    static void Main()
    {
        SQLitePCL.Batteries_V2.Init(); // ten sam init co CLI - inaczej SQLCipher nie wystartuje
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

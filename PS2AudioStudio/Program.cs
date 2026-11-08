using System;
using System.Windows.Forms;

namespace PS2AudioStudio
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.ToString(), "Unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                MessageBox.Show(e.ExceptionObject?.ToString(), "Fatal error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            var form = new MainForm();
            // files passed on the command line / "Open with..." / dropped on the .exe
            if (args.Length > 0) form.Shown += (s, e) => form.OpenPaths(args);

            AppIcon.HookAll();
            DragOut.CleanOld();

            Application.Run(form);
        }
    }
}

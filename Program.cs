using System;
using System.Windows.Forms;

namespace LinkDownloader
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += delegate (object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "Unexpected error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += delegate (object sender, UnhandledExceptionEventArgs e)
            {
                Exception ex = e.ExceptionObject as Exception;
                if (ex != null)
                {
                    MessageBox.Show(ex.Message, "Unexpected error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };

            AppSettings settings = AppSettings.Load();
            Application.Run(new MainForm(settings));
        }
    }
}

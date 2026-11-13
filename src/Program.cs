using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SshTunnelManager
{
    internal static class Program
    {
        private static string CrashLogPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SshTunnelManager", "crash.log");
            }
        }

        [STAThread]
        private static void Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                WriteCrash("AppDomain", e.ExceptionObject as Exception);

            Application.ThreadException += (s, e) =>
            {
                WriteCrash("ThreadException", e.Exception);
                MessageBox.Show(e.Exception.Message, "Lỗi", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                var options = CommandLineOptions.Parse(Environment.GetCommandLineArgs());
                Application.Run(new MainForm(options));
            }
            catch (Exception ex)
            {
                WriteCrash("Main", ex);
                MessageBox.Show("Không khởi động được:\n\n" + ex, "Lỗi khởi động",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void WriteCrash(string source, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath));
                var sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + source + "] ===");
                sb.AppendLine(ex != null ? ex.ToString() : "(no exception object)");
                sb.AppendLine();
                File.AppendAllText(CrashLogPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}

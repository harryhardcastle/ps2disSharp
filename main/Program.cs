using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace PS2Disassembler
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 1 && (args[0] == "--test" || args[0] == "-t"))
            {
                DisassemblerTests.Run();
                return;
            }

            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.SystemAware);

            Application.ThreadException += (_, e) =>
            {
                WriteStartupLog("UI thread exception", e.Exception);
                MessageBox.Show(
                    "ps2dis# hit an unhandled UI exception during startup.\n\n" +
                    "A log was written to ps2dissharp_startup.log next to the executable.",
                    "ps2dis#",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            };

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                WriteStartupLog("Non-UI unhandled exception", e.ExceptionObject as Exception);
            };

            string? startFile = (args.Length >= 1 && System.IO.File.Exists(args[0]))
                                ? args[0] : null;

            try
            {
                Application.Run(new MainForm(startFile));
            }
            catch (Exception ex)
            {
                WriteStartupLog("Fatal startup exception", ex);
                MessageBox.Show(
                    "ps2dis# failed to start.\n\n" +
                    "A log was written to ps2dissharp_startup.log next to the executable.",
                    "ps2dis#",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static void WriteStartupLog(string heading, Exception? ex)
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;
                string path = Path.Combine(baseDir, "ps2dissharp_startup.log");
                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {heading}");
                if (ex != null)
                {
                    sb.AppendLine(ex.ToString());
                }
                else
                {
                    sb.AppendLine("No exception object was available.");
                }
                sb.AppendLine(new string('-', 72));
                File.AppendAllText(path, sb.ToString());
            }
            catch
            {
            }
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace QuickAdd
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].TrimStart('-', '/').ToLowerInvariant() == "install-dock")
            {
                InstallDockFromCommandLine();
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "NexusQuickAdd.SingleInstance", out created))
            {
                if (!created)
                {
                    IntPtr existing = Native.FindWindow(null, AppInfo.Name);
                    if (existing != IntPtr.Zero) Native.SetForegroundWindow(existing);
                    return;
                }

                AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
                {
                    LogCrash(e.ExceptionObject as Exception);
                };

                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.DispatcherUnhandledException += delegate (object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.Message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
                    e.Handled = true;
                };
                try
                {
                    app.Run(new MainWindow());
                }
                catch (Exception ex)
                {
                    LogCrash(ex);
                    throw;
                }
            }
        }

        /// <summary>NexusQuickAdd.exe --install-dock : put the tool on the dock and exit.</summary>
        private static void InstallDockFromCommandLine()
        {
            Config config = Config.Load();
            NexusEnv.Initialize(config);
            ApplyResult result = NexusDock.InstallSelf();
            string message;
            int code;
            if (!string.IsNullOrEmpty(result.Error))
            {
                message = result.Error;
                code = 1;
            }
            else if (result.Added == 0)
            {
                message = "停靠栏里已经有「" + AppInfo.DockLabel + "」了。";
                code = 0;
            }
            else
            {
                message = string.Format("已把「{0}」固定到停靠栏（第 {1} 项）{2}",
                    AppInfo.DockLabel, result.Added,
                    result.Restarted ? (result.Verified ? "，Nexus 已重启。" : "，Nexus 正在重启。") : "，下次启动 Nexus 生效。");
                code = 0;
            }
            AttachConsole(-1);
            Console.WriteLine(message);
            if (Environment.UserInteractive && !HasConsole())
                MessageBox.Show(message, AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Information);
            Environment.Exit(code);
        }

        internal static void LogCrash(Exception ex)
        {
            try
            {
                string file = System.IO.Path.Combine(NexusEnv.DataDir, "crash.log");
                System.IO.File.AppendAllText(file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + (ex == null ? "(unknown)" : ex.ToString()) + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        private static bool HasConsole()
        {
            return GetConsoleWindow() != IntPtr.Zero;
        }
    }
}

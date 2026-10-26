using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

namespace QuickAdd.Setup
{
    internal static class SetupInfo
    {
        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "nq-setup.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine);
            }
            catch { }
        }

        public const string Version = "1.1.0";
        public const string Title = "Nexus 速添版 · 安装";
        public const string DownloadUrl = "https://www.winstep.net/nexus.zip";
        public const string ToolExeName = "NexusQuickAdd.exe";
        public const string DockLabel = "速添";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\NexusQuickAdd";
    }

    internal static class SetupProgram
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && args[0].TrimStart('-', '/').ToLowerInvariant() == "uninstall")
            {
                Environment.Exit(Uninstaller.Run());
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                SetupInfo.Log("UNHANDLED: " + e.ExceptionObject);
            };

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.DispatcherUnhandledException += delegate (object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, SetupInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            app.Run(new SetupWindow());
        }
    }

    /// <summary>Detects Nexus, installs the tool and wires it into the dock.</summary>
    internal static class Installer
    {
        public static string FindNexus()
        {
            try
            {
                string[] subs =
                {
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Nexus",
                    @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Nexus"
                };
                foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
                {
                    foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                    {
                        try
                        {
                            using (RegistryKey b = RegistryKey.OpenBaseKey(hive, view))
                            {
                                foreach (string sub in subs)
                                {
                                    using (RegistryKey k = b.OpenSubKey(sub))
                                    {
                                        if (k == null) continue;
                                        string loc = Convert.ToString(k.GetValue("InstallLocation")).Trim().TrimEnd('\\');
                                        if (loc.Length > 0 && File.Exists(Path.Combine(loc, "Nexus.exe"))) return loc;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            try
            {
                foreach (Process p in Process.GetProcessesByName("Nexus"))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return Path.GetDirectoryName(path);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }

            var candidates = new List<string>();
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Winstep"));
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Winstep"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Winstep"));
            candidates.Add(@"C:\Nexus\Winstep");
            candidates.Add(@"D:\Nexus\Winstep");
            foreach (string c in candidates)
                if (File.Exists(Path.Combine(c, "Nexus.exe"))) return c;

            return null;
        }

        public static string ToolDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusQuickAdd");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ToolPath
        {
            get { return Path.Combine(ToolDir, SetupInfo.ToolExeName); }
        }

        /// <summary>Nexus is installed silently; falls back to the interactive wizard.</summary>
        public static void EnsureNexus(Action<string> report, Action<int> progress)
        {
            string found = FindNexus();
            if (found != null)
            {
                report("已检测到 Nexus：" + found);
                return;
            }

            string setup = ExtractBundledInstaller();
            if (setup == null)
            {
                report("未安装 Nexus，正在下载官方安装包（约 41 MB）…");
                setup = DownloadInstaller(report, progress);
            }
            else
            {
                report("未安装 Nexus，使用内置安装包…");
            }

            report("正在安装 Nexus（静默）…");
            progress(-1);
            RunAndWait(setup, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-", 600000);

            for (int i = 0; i < 90 && FindNexus() == null; i++) Thread.Sleep(1000);
            if (FindNexus() != null)
            {
                report("Nexus 已安装：" + FindNexus());
                return;
            }

            report("静默安装未完成，正在打开 Nexus 安装向导，请按提示完成…");
            RunAndWait(setup, "", 1800000);
            for (int i = 0; i < 60 && FindNexus() == null; i++) Thread.Sleep(1000);
            if (FindNexus() == null)
                throw new InvalidOperationException("仍未检测到 Nexus。请手动安装 Nexus 后重新运行本安装器。");
            report("Nexus 已安装：" + FindNexus());
        }

        private static string ExtractBundledInstaller()
        {
            try
            {
                Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("nexus-setup.exe");
                if (s == null) return null;
                string path = Path.Combine(Path.GetTempPath(), "nexus-setup-bundled.exe");
                using (s)
                using (FileStream f = File.Create(path))
                    s.CopyTo(f);
                return path;
            }
            catch { return null; }
        }

        private static string DownloadInstaller(Action<string> report, Action<int> progress)
        {
            string zip = Path.Combine(Path.GetTempPath(), "nexus-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".zip");
            try
            {
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "NexusQuickAdd-Setup");
                    var done = new ManualResetEvent(false);
                    Exception error = null;
                    client.DownloadProgressChanged += delegate (object s, DownloadProgressChangedEventArgs e)
                    {
                        if (progress != null) progress(e.ProgressPercentage);
                    };
                    client.DownloadFileCompleted += delegate (object s, System.ComponentModel.AsyncCompletedEventArgs e)
                    {
                        error = e.Error;
                        done.Set();
                    };
                    client.DownloadFileAsync(new Uri(SetupInfo.DownloadUrl), zip);
                    done.WaitOne();
                    if (error != null) throw error;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "下载 Nexus 安装包失败（" + ex.Message + "）。\n可以手动安装 Nexus 后重新运行本安装器，或直接访问 https://www.winstep.net/nexus.asp");
            }

            string dir = Path.Combine(Path.GetTempPath(), "nexus-extract-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(dir);
            ExtractZip(zip, dir);

            string setup = FindFile(dir, "NexusSetup.exe", 4);
            if (setup == null) throw new InvalidOperationException("安装包里没有找到 NexusSetup.exe");
            return setup;
        }

        private static void ExtractZip(string zip, string target)
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) throw new InvalidOperationException("无法解压下载的安装包（Shell.Application 不可用）");
            object shell = Activator.CreateInstance(shellType);
            object source = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { zip });
            object dest = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { target });
            if (source == null || dest == null) throw new InvalidOperationException("无法打开下载的压缩包");
            object items = source.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, source, null);
            dest.GetType().InvokeMember("CopyHere", BindingFlags.InvokeMethod, null, dest, new object[] { items, 0x14 });

            for (int i = 0; i < 300; i++)
            {
                if (FindFile(target, "NexusSetup.exe", 4) != null) return;
                Thread.Sleep(200);
            }
        }

        private static string FindFile(string dir, string name, int depth)
        {
            try
            {
                string direct = Path.Combine(dir, name);
                if (File.Exists(direct)) return direct;
                if (depth <= 0) return null;
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string hit = FindFile(sub, name, depth - 1);
                    if (hit != null) return hit;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Copies the embedded tool next to the user profile and registers an uninstall entry.</summary>
        public static string InstallTool(Action<string> report)
        {
            string dir = ToolDir;
            string exe = ToolPath;
            // the tool may be open (dock icon) - close it before replacing the exe
            foreach (Process running in Process.GetProcessesByName("NexusQuickAdd"))
            {
                try { running.Kill(); running.WaitForExit(4000); }
                catch { }
            }
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("tool.exe"))
            {
                if (s == null) throw new InvalidOperationException("安装包里缺少 NexusQuickAdd.exe");
                using (FileStream f = File.Create(exe)) s.CopyTo(f);
            }
            report("速添已安装到 " + dir);

            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(SetupInfo.UninstallKey))
                {
                    if (k != null)
                    {
                        k.SetValue("DisplayName", "Nexus 速添 (Nexus QuickAdd)");
                        k.SetValue("DisplayVersion", SetupInfo.Version);
                        k.SetValue("Publisher", "late-autumn-gif");
                        k.SetValue("DisplayIcon", exe);
                        k.SetValue("InstallLocation", dir);
                        k.SetValue("UninstallString", "\"" + Assembly.GetExecutingAssembly().Location + "\" --uninstall");
                        k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
            return exe;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        /// <summary>After a fresh install Nexus is not running yet - start it so the user sees the dock.</summary>
        public static void EnsureNexusRunning(string nexusDir, Action<string> report)
        {
            if (Process.GetProcessesByName("Nexus").Length > 0) return;
            string exe = Path.Combine(nexusDir, "Nexus.exe");
            if (!File.Exists(exe)) return;
            report("正在启动 Nexus…");
            var psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = nexusDir;
            psi.UseShellExecute = false;
            psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            psi.EnvironmentVariables.Remove("NODE_OPTIONS");
            try { Process.Start(psi); } catch { return; }
            for (int i = 0; i < 80; i++)
            {
                if (FindWindow("ThunderRT5Form", "NxDock") != IntPtr.Zero) return;
                Thread.Sleep(250);
            }
        }
        public static void IntegrateDock(string toolExe, Action<string> report)
        {
            report("正在写入 Nexus 停靠栏…");
            int code = RunAndWait(toolExe, "--install-dock quiet", 180000);
            if (code != 0) throw new InvalidOperationException("写入停靠栏失败（退出码 " + code + "）。可以稍后打开速添，点「加入停靠栏」重试。");
            report("已加入停靠栏。");
        }

        public static int RunAndWait(string exe, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo(exe, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(timeoutMs)) return -1;
                return p.ExitCode;
            }
        }
    }

    internal static class Uninstaller
    {
        public static int Run()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusQuickAdd");
            string tool = Path.Combine(dir, SetupInfo.ToolExeName);
            try
            {
                if (File.Exists(tool)) Installer.RunAndWait(tool, "--remove-dock quiet", 180000);
            }
            catch { }

            try { Registry.CurrentUser.DeleteSubKeyTree(SetupInfo.UninstallKey, false); }
            catch { }

            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                try
                {
                    Process.Start(new ProcessStartInfo("cmd.exe",
                        "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"") { CreateNoWindow = true, UseShellExecute = false });
                }
                catch { }
            }

            MessageBox.Show("已卸载 Nexus 速添，停靠栏项目已移除。", SetupInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }
    }

    public class SetupWindow : Window
    {
        private Grid _root;
        private TextBlock _detect, _detail, _step1, _step2, _step3, _version;
        private Border _detectBox;
        private System.Windows.Shapes.Ellipse _dot1, _dot2, _dot3;
        private ProgressBar _progress;
        private Button _primary, _uninstall, _close;
        private bool _busy;
        private bool _installed;

        public SetupWindow()
        {
            InitWindow();
            InitUi();
            DetectState();
            Wire();
        }

        private void InitWindow()
        {
            Title = SetupInfo.Title;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Width = 700;
            Height = 470;
            ShowInTaskbar = true;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            try
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            catch { }
        }

        private void InitUi()
        {
            string xaml;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("setup.xaml"))
            {
                if (s == null) throw new InvalidOperationException("setup.xaml missing");
                using (var r = new StreamReader(s, Encoding.UTF8)) xaml = r.ReadToEnd();
            }
            _root = (Grid)XamlReader.Parse(xaml);
            Content = _root;

            _detect = (TextBlock)Find("DetectText");
            _detail = (TextBlock)Find("DetailText");
            _step1 = (TextBlock)Find("Step1");
            _step2 = (TextBlock)Find("Step2");
            _step3 = (TextBlock)Find("Step3");
            _dot1 = (System.Windows.Shapes.Ellipse)Find("Dot1");
            _dot2 = (System.Windows.Shapes.Ellipse)Find("Dot2");
            _dot3 = (System.Windows.Shapes.Ellipse)Find("Dot3");
            _progress = (ProgressBar)Find("Progress");
            _primary = (Button)Find("PrimaryButton");
            _uninstall = (Button)Find("UninstallButton");
            _close = (Button)Find("CloseButton");
            _detectBox = (Border)Find("DetectBox");
            _version = (TextBlock)Find("VersionText");
            _version.Text = "v" + SetupInfo.Version;
        }

        private void Wire()
        {
            Loaded += delegate
            {
                _root.Opacity = 0;
                var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
                _root.BeginAnimation(OpacityProperty, fade);
            };
            Closed += delegate { SetupInfo.Log("window closed"); };
            AppDomain.CurrentDomain.ProcessExit += delegate { SetupInfo.Log("process exit"); };
            FrameworkElement header = (FrameworkElement)Find("HeaderBar");
            header.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch { } }
            };
            _close.Click += delegate { Close(); };
            _primary.Click += delegate
            {
                if (_installed) { OpenTool(); return; }
                StartInstall();
            };
            _uninstall.Click += delegate { Uninstall(); };
        }

        private void DetectState()
        {
            string nexus = Installer.FindNexus();
            bool toolExists = File.Exists(Installer.ToolPath);
            if (nexus != null && toolExists)
            {
                _detect.Text = "Nexus：" + nexus + "\n速添：" + Installer.ToolDir + "（已安装，可重新写入停靠栏）";
                _primary.Content = "重新安装";
            }
            else if (nexus != null)
            {
                _detect.Text = "已检测到 Nexus：" + nexus + "\n点击一键安装，把速添装好并加入停靠栏。";
                _primary.Content = "一键安装";
            }
            else
            {
                _detectBox.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF7, 0xE8));
                _detectBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xDD, 0xB3));
                _detect.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x5B, 0x00));
                _detect.Text = "未检测到 Winstep Nexus。\n安装时会自动下载官方安装包（约 41 MB）并静默安装，装好后自动加入停靠栏。";
                _primary.Content = "安装 Nexus + 速添";
            }
        }

        private void StartInstall()
        {
            SetupInfo.Log("install: start");
            if (_busy) return;
            _busy = true;
            _primary.IsEnabled = false;
            _uninstall.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _progress.IsIndeterminate = true;
            _detail.Text = "";
            SetStep(0, true);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Installer.EnsureNexus(Report, OnProgress);
                    Dispatcher.Invoke(new Action(delegate { SetStep(0, false); SetStep(1, true); _progress.IsIndeterminate = true; }));
                    string tool = Installer.InstallTool(Report);
                    Dispatcher.Invoke(new Action(delegate { SetStep(1, false); SetStep(2, true); }));
                    Installer.IntegrateDock(tool, Report);
                    string nexusDir = Installer.FindNexus();
                    if (nexusDir != null) Installer.EnsureNexusRunning(nexusDir, Report);
                    Dispatcher.Invoke(new Action(Finish));
                }
                catch (Exception ex)
                {
                    SetupInfo.Log("install: failed " + ex);
                    Dispatcher.Invoke(new Action(delegate { Fail(ex.Message); }));
                }
            });
        }

        private void Report(string message)
        {
            Dispatcher.BeginInvoke(new Action(delegate { _detail.Text = message; }));
        }

        private void OnProgress(int percent)
        {
            Dispatcher.BeginInvoke(new Action(delegate
            {
                if (percent < 0) { _progress.IsIndeterminate = true; return; }
                _progress.IsIndeterminate = false;
                _progress.Value = percent;
            }));
        }

        private void SetStep(int index, bool active)
        {
            TextBlock text = index == 0 ? _step1 : index == 1 ? _step2 : _step3;
            System.Windows.Shapes.Ellipse dot = index == 0 ? _dot1 : index == 1 ? _dot2 : _dot3;
            if (active)
            {
                text.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                text.FontWeight = FontWeights.SemiBold;
                dot.Fill = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            }
            else
            {
                text.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x9A, 0x5B));
                text.FontWeight = FontWeights.Normal;
                dot.Fill = new SolidColorBrush(Color.FromRgb(0x15, 0x9A, 0x5B));
            }
        }

        private void Finish()
        {
            SetupInfo.Log("install: finish");
            _busy = false;
            _installed = true;
            _progress.IsIndeterminate = false;
            _progress.Value = 100;
            _step1.Foreground = _step2.Foreground = _step3.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x9A, 0x5B));
            _dot1.Fill = _dot2.Fill = _dot3.Fill = new SolidColorBrush(Color.FromRgb(0x15, 0x9A, 0x5B));
            _detectBox.Background = new SolidColorBrush(Color.FromRgb(0xF2, 0xFB, 0xF5));
            _detectBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xEF, 0xD9));
            _detect.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x9A, 0x5B));
            _detect.Text = "安装完成 —— 停靠栏里已经有「" + SetupInfo.DockLabel + "」，点它就能搜索并添加程序。";
            _detail.Text = "安装位置：" + Installer.ToolDir;
            _primary.Content = "打开速添";
            _primary.IsEnabled = true;
            _uninstall.IsEnabled = true;
            SetupInfo.Log("install: finish done");
        }

        private void Fail(string message)
        {
            _busy = false;
            _primary.IsEnabled = true;
            _uninstall.IsEnabled = true;
            _progress.IsIndeterminate = false;
            _progress.Value = 0;
            _detectBox.Background = new SolidColorBrush(Color.FromRgb(0xFD, 0xF2, 0xF2));
            _detectBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF8, 0xC8, 0xC8));
            _detect.Foreground = new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18));
            _detect.Text = "安装未完成";
            _detail.Text = message;
        }

        private void OpenTool()
        {
            try { Process.Start(Installer.ToolPath); Close(); }
            catch (Exception ex) { _detail.Text = "启动失败：" + ex.Message; }
        }

        private void Uninstall()
        {
            var answer = MessageBox.Show("卸载 Nexus 速添，并从停靠栏移除？\n（Nexus 本身不会被卸载）", SetupInfo.Title,
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
            try
            {
                if (File.Exists(Installer.ToolPath)) Installer.RunAndWait(Installer.ToolPath, "--remove-dock quiet", 180000);
                Registry.CurrentUser.DeleteSubKeyTree(SetupInfo.UninstallKey, false);
                string dir = Installer.ToolDir;
                try { Directory.Delete(dir, true); }
                catch
                {
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
                    { CreateNoWindow = true, UseShellExecute = false });
                }
                MessageBox.Show("已卸载，停靠栏项目已移除。", SetupInfo.Title, MessageBoxButton.OK, MessageBoxImage.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("卸载失败：" + ex.Message, SetupInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private FrameworkElement Find(string name)
        {
            return FindIn(_root, name);
        }

        private static FrameworkElement FindIn(DependencyObject root, string name)
        {
            FrameworkElement fe = root as FrameworkElement;
            if (fe != null && fe.Name == name) return fe;
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                DependencyObject d = child as DependencyObject;
                if (d == null) continue;
                FrameworkElement found = FindIn(d, name);
                if (found != null) return found;
            }
            return null;
        }
    }
}

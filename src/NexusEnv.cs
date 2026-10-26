using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace QuickAdd
{
    /// <summary>
    /// Finds the Winstep Nexus installation on this machine and owns the tool's own data folder.
    /// Everything here is discovered at runtime so the exe works on any PC, not just the dev box.
    /// </summary>
    public static class NexusEnv
    {
        public const string DocksKey = @"Software\WinSTEP2000\NeXuS\Docks";

        public static string InstallDir { get; private set; }
        public static string ExePath { get; private set; }
        public static bool Found { get; private set; }
        public static bool DockConfigured { get; private set; }

        public static string DataDir
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusQuickAdd");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                return dir;
            }
        }

        public static string ShortcutDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "shortcuts");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                return dir;
            }
        }

        public static string BackupDir
        {
            get
            {
                string dir = Path.Combine(DataDir, "backups");
                try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); }
                catch { }
                return dir;
            }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(DataDir, "config.ini"); }
        }

        /// <summary>Optional portable override that sits next to the exe.</summary>
        public static string PortableConfigPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.txt"); }
        }

        public static void Initialize(Config config)
        {
            DockConfigured = DockKeyExists();
            string dir = null;

            if (config != null && !string.IsNullOrEmpty(config.NexusDir))
                dir = Validate(config.NexusDir);
            if (dir == null) dir = Validate(RunningNexus());
            if (dir == null) dir = FromRegistry();
            if (dir == null) dir = FromCommonPaths();

            InstallDir = dir;
            ExePath = dir == null ? null : Path.Combine(dir, "Nexus.exe");
            Found = dir != null;
        }

        private static bool DockKeyExists()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DocksKey)) return k != null;
            }
            catch { return false; }
        }

        private static string Validate(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return null;
            try
            {
                dir = dir.Trim().Trim('"').TrimEnd('\\');
                if (File.Exists(Path.Combine(dir, "Nexus.exe"))) return dir;
            }
            catch { }
            return null;
        }

        private static string RunningNexus()
        {
            try
            {
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName("Nexus"))
                {
                    try
                    {
                        string path = p.MainModule.FileName;
                        if (!string.IsNullOrEmpty(path)) return Path.GetDirectoryName(path);
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
            return null;
        }

        private static string FromRegistry()
        {
            string[] subKeys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Nexus",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Nexus",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{Nexus}_is1"
            };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey b = RegistryKey.OpenBaseKey(hive, view))
                        {
                            foreach (string sub in subKeys)
                            {
                                using (RegistryKey k = b.OpenSubKey(sub))
                                {
                                    if (k == null) continue;
                                    string hit = Validate(Convert.ToString(k.GetValue("InstallLocation")));
                                    if (hit != null) return hit;
                                    hit = Validate(Path.GetDirectoryName(Convert.ToString(k.GetValue("DisplayIcon")).Trim('"')));
                                    if (hit != null) return hit;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        private static string FromCommonPaths()
        {
            var candidates = new List<string>();
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Winstep"));
            if (!string.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Winstep"));
            if (!string.IsNullOrEmpty(local)) candidates.Add(Path.Combine(local, "Winstep"));
            candidates.Add(@"C:\Nexus\Winstep");
            candidates.Add(@"D:\Nexus\Winstep");
            candidates.Add(@"C:\Winstep");
            foreach (string c in candidates)
            {
                string hit = Validate(c);
                if (hit != null) return hit;
            }
            return null;
        }
    }

    /// <summary>Small key=value config: %LOCALAPPDATA%\NexusQuickAdd\config.ini, overridable by settings.txt beside the exe.</summary>
    public class Config
    {
        public string NexusDir = "";
        public string[] Roots;
        public int Depth = 3;
        public string Hotkey = "Ctrl+Alt+N";
        public bool BannerDismissed;
        public bool DockInstalled;

        public static Config Load()
        {
            var c = new Config();
            c.ReadFile(NexusEnv.ConfigPath, true);
            c.ReadFile(NexusEnv.PortableConfigPath, false);
            if (c.Roots == null || c.Roots.Length == 0) c.Roots = DefaultRoots();
            if (c.Depth < 1) c.Depth = 1;
            if (c.Depth > 5) c.Depth = 5;
            return c;
        }

        private void ReadFile(string path, bool includeOwnKeys)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = line.Substring(eq + 1).Trim();
                    switch (key)
                    {
                        case "nexus": NexusDir = val; break;
                        case "roots":
                            var list = new List<string>();
                            foreach (string p in val.Split(';')) if (p.Trim().Length > 0) list.Add(p.Trim());
                            if (list.Count > 0) Roots = list.ToArray();
                            break;
                        case "depth": int d; if (int.TryParse(val, out d)) Depth = d; break;
                        case "hotkey": if (val.Length > 0) Hotkey = val; break;
                        case "bannerdismissed": if (includeOwnKeys) BannerDismissed = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                        case "dockinstalled": if (includeOwnKeys) DockInstalled = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            catch { }
        }

        public void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Nexus 速添 / Nexus QuickAdd");
                sb.AppendLine("# nexus  : Nexus 安装目录（自动检测失败时手动指定）");
                sb.AppendLine("# roots  : 便携程序扫描根目录，分号分隔；留空自动使用所有本地磁盘");
                sb.AppendLine("# depth  : 扫描深度 1-5");
                sb.AppendLine("# hotkey : 呼出窗口的热键");
                sb.AppendLine("nexus=" + NexusDir);
                sb.AppendLine("roots=" + string.Join(";", Roots));
                sb.AppendLine("depth=" + Depth);
                sb.AppendLine("hotkey=" + Hotkey);
                sb.AppendLine("dockInstalled=" + (DockInstalled ? 1 : 0));
                sb.AppendLine("bannerDismissed=" + (BannerDismissed ? 1 : 0));
                File.WriteAllText(NexusEnv.ConfigPath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>All local fixed drives — the portable-app guess that works on a fresh machine.</summary>
        public static string[] DefaultRoots()
        {
            var roots = new List<string>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType == DriveType.Fixed && d.IsReady) roots.Add(d.RootDirectory.FullName);
                    }
                    catch { }
                }
            }
            catch { }
            if (roots.Count == 0) roots.Add("C:\\");
            return roots.ToArray();
        }
    }
}

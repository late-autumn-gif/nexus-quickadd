using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace QuickAdd
{
    public class DockItem
    {
        public int Index;
        public string Path = "";
        public string Label = "";
        public string StartPath = "";
    }

    public class ApplyResult
    {
        public int Added;
        public string Backup = "";
        public bool Restarted;
        public bool Verified;
        public string Error = "";
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// Reads and writes the Nexus dock item list.
    /// Storage (reverse-engineered, see docs/nexus-dock-format.md):
    ///   HKCU\Software\WinSTEP2000\NeXuS\Docks
    ///     DockNoItems1        highest item index (0 based)
    ///     1Path{i} 1Label{i} 1Type{i}  [1StartPath{i}]
    /// Nexus builds the row from the registry at start-up, so changes show up after a restart.
    /// </summary>
    public static class NexusDock
    {
        private const string KeyPath = NexusEnv.DocksKey;
        private const string NoItemsValue = "DockNoItems1";

        public static string InstallDir
        {
            get { return NexusEnv.InstallDir; }
        }

        public static string ExePath
        {
            get { return NexusEnv.ExePath; }
        }

        public static bool IsRunning
        {
            get { return Process.GetProcessesByName("Nexus").Length > 0; }
        }

        // ---------- read ----------

        public static int HighestIndex()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return -1;
                    int n;
                    if (int.TryParse(Convert.ToString(k.GetValue(NoItemsValue)), out n)) return n;
                }
            }
            catch { }
            return -1;
        }

        public static List<DockItem> Read()
        {
            var list = new List<DockItem>();
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    if (k == null) return list;
                    int max;
                    if (!int.TryParse(Convert.ToString(k.GetValue(NoItemsValue)), out max)) return list;
                    for (int i = 0; i <= max; i++)
                    {
                        string path = Convert.ToString(k.GetValue("1Path" + i));
                        if (string.IsNullOrEmpty(path)) continue;
                        var it = new DockItem();
                        it.Index = i;
                        it.Path = path;
                        it.Label = Convert.ToString(k.GetValue("1Label" + i));
                        it.StartPath = Convert.ToString(k.GetValue("1StartPath" + i));
                        list.Add(it);
                    }
                }
            }
            catch { }
            return list;
        }

        public static int ItemCount()
        {
            return Read().Count;
        }

        /// <summary>
        /// Keys for everything already on the dock: "target|" for plain programs and
        /// "target|args" for entries launched with arguments (packaged apps).
        /// </summary>
        public static HashSet<string> DockKeys()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DockItem it in Read())
            {
                set.Add(Normalize(it.Path) + "|");
                if (it.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string target, args, work;
                        Native.ReadShortcut(it.Path, out target, out args, out work);
                        if (string.IsNullOrEmpty(target)) continue;
                        set.Add(Normalize(target) + "|");
                        if (!string.IsNullOrEmpty(args)) set.Add(Normalize(target) + "|" + Normalize(args));
                    }
                    catch { }
                }
            }
            return set;
        }

        public static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            return p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        // ---------- self registration ----------

        public static string SelfPath
        {
            get { return System.Reflection.Assembly.GetExecutingAssembly().Location; }
        }

        /// <summary>True when this tool already has its own icon on the dock.</summary>
        public static bool HasSelfItem()
        {
            string self = Normalize(SelfPath);
            foreach (DockItem it in Read())
            {
                if (Normalize(it.Path) == self) return true;
                if (it.Path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string target, args, work;
                        Native.ReadShortcut(it.Path, out target, out args, out work);
                        if (Normalize(target) == self) return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        /// <summary>Puts this tool on the dock so it opens like any other Nexus item.</summary>
        public static ApplyResult InstallSelf()
        {
            if (!NexusEnv.Found) return Error("没有找到 Winstep Nexus，请先安装 Nexus 后再试");
            if (HasSelfItem()) return Error("");
            var e = new Entry();
            e.Name = AppInfo.DockLabel;
            e.Target = SelfPath;
            e.WorkDir = Path.GetDirectoryName(SelfPath);
            return Apply(new List<Entry> { e });
        }

        private static ApplyResult Error(string message)
        {
            var r = new ApplyResult();
            r.Error = message;
            return r;
        }

        // ---------- write ----------

        public static ApplyResult Apply(List<Entry> entries)
        {
            var result = new ApplyResult();
            if (entries == null || entries.Count == 0) { result.Error = "没有待添加的项目"; return result; }
            if (ExePath == null) { result.Error = "没有找到 Nexus 安装目录"; return result; }

            bool wasRunning = IsRunning;
            try
            {
                result.Backup = Backup();
            }
            catch (Exception ex)
            {
                result.Error = "备份失败，已取消写入：" + ex.Message;
                return result;
            }

            try
            {
                if (wasRunning)
                {
                    Stop();
                    if (IsRunning)
                    {
                        result.Error = "无法结束 Nexus 进程，改动未写入（否则会被运行中的 Nexus 覆盖）";
                        return result;
                    }
                }

                int max = HighestIndex();
                Directory.CreateDirectory(NexusEnv.ShortcutDir);
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    if (k == null) { result.Error = "无法打开 Nexus 注册表配置"; return result; }
                    foreach (Entry e in entries)
                    {
                        int idx = ++max;
                        string target = e.Target;
                        bool needsShortcut = !string.IsNullOrEmpty(e.Args) ||
                                             string.Equals(Path.GetFileName(e.Target), "explorer.exe", StringComparison.OrdinalIgnoreCase);
                        if (needsShortcut) target = WriteShortcut(e);

                        k.SetValue("1Path" + idx, target, RegistryValueKind.String);
                        k.SetValue("1Label" + idx, e.Name, RegistryValueKind.String);
                        k.SetValue("1Type" + idx, "1", RegistryValueKind.String);

                        string startDir = e.Dir;
                        string exeDir = "";
                        try { exeDir = Path.GetDirectoryName(e.Target); } catch { }
                        if (!string.IsNullOrEmpty(startDir) && !string.IsNullOrEmpty(exeDir) &&
                            !string.Equals(startDir.TrimEnd('\\'), exeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            k.SetValue("1StartPath" + idx, startDir, RegistryValueKind.String);
                        }
                        result.Added++;
                        result.Notes.Add(string.Format("[{0}] {1} -> {2}", idx, e.Name, target));
                    }
                    k.SetValue(NoItemsValue, max.ToString(), RegistryValueKind.String);
                }
            }
            catch (Exception ex)
            {
                result.Error = "写入失败：" + ex.Message;
            }
            finally
            {
                if (wasRunning)
                {
                    try
                    {
                        Start();
                        result.Restarted = true;
                        result.Verified = WaitForDock(20000);
                    }
                    catch (Exception ex)
                    {
                        result.Notes.Add("重启 Nexus 失败：" + ex.Message);
                    }
                }
                else
                {
                    result.Restarted = false;
                    result.Notes.Add("Nexus 当前未运行，改动会在下次启动时生效");
                }
            }
            return result;
        }

        private static string WriteShortcut(Entry e)
        {
            string name = e.Name;
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(e.Target);
            string lnk = Path.Combine(NexusEnv.ShortcutDir, name + ".lnk");
            Native.CreateShortcut(lnk, e.Target, e.Args, e.Dir, e.Target);
            return lnk;
        }

        public static string Backup()
        {
            Directory.CreateDirectory(NexusEnv.BackupDir);
            string file = Path.Combine(NexusEnv.BackupDir, "winstep-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".reg");
            var psi = new ProcessStartInfo("reg.exe", "export \"HKCU\\Software\\WinSTEP2000\" \"" + file + "\" /y");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit(8000);
            }
            if (!File.Exists(file)) throw new IOException("reg.exe export produced no file");
            return file;
        }

        public static void Stop()
        {
            foreach (Process p in Process.GetProcessesByName("Nexus"))
            {
                try { p.Kill(); } catch { }
                p.WaitForExit(6000);
            }
            for (int i = 0; i < 40 && IsRunning; i++) Thread.Sleep(100);
        }

        public static void Start()
        {
            var psi = new ProcessStartInfo(ExePath);
            psi.WorkingDirectory = InstallDir;
            psi.UseShellExecute = false;
            // inherited Electron variables make dock-launched Electron apps exit instantly
            psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            psi.EnvironmentVariables.Remove("NODE_OPTIONS");
            Process.Start(psi);
        }

        private static bool WaitForDock(int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (Native.FindWindow("ThunderRT5Form", "NxDock") != IntPtr.Zero) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            return false;
        }
    }
}

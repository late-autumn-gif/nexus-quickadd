using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace QuickAdd
{
    /// <summary>Collects launchable programs from the shell, the registry, game stores and disk.</summary>
    public class Indexer
    {
        public readonly List<Entry> Entries = new List<Entry>();
        private readonly Dictionary<string, Entry> _byTarget = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> _byKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Entry> _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        /// <summary>Thread-safe copy for the UI while indexing is still running.</summary>
        public List<Entry> Snapshot()
        {
            lock (_gate) { return new List<Entry>(Entries); }
        }

        private static readonly string[] ExeBlacklist =
        {
            "unins", "uninstall", "setup", "install", "update", "updater", "crash", "report",
            "vcredist", "dxsetup", "dxwebsetup", "redist", "node.exe", "python.exe", "pythonw.exe",
            "7z.exe", "7za.exe", "ffmpeg", "ffplay", "adb.exe", "java.exe", "javaw.exe",
            "msiexec", "regsvr32", "cmd.exe", "powershell", "conhost", "help", "readme", "dotnet.exe"
        };

        // common Chinese <-> western name pairs so one program answers to both spellings
        private static readonly string[][] AliasTable =
        {
            new string[] { "微信输入法", "wetype" },
            new string[] { "微信", "wechat", "weixin" },
            new string[] { "腾讯会议", "wemeet" },
            new string[] { "网易云", "netease", "cloudmusic" },
            new string[] { "雷神加速", "leigod" },
            new string[] { "青空次元", "sorani" },
            new string[] { "动漫共和国", "dmghg" },
            new string[] { "米哈游", "mihoyo" },
            new string[] { "无畏契约", "valorant", "aclos" },
            new string[] { "腾讯", "tencent" }
        };
        private static readonly string[] DirBlacklist =
        {
            "windows", "$recycle.bin", "system volume information", "node_modules", ".git", ".svn",
            "__pycache__", "temp", "tmp", "logs", "cache", "installer", "downloads", "backup",
            "appdata", "packages", "symbols", "redist", "vcredist"
        };

        public Action<int> Progress;

        /// <summary>Fast shell/registry sources — this is what the list opens with.</summary>
        public void BuildQuick()
        {
            var timings = new System.Collections.Generic.List<string>();
            Step("start menu", timings, CollectStartMenu);
            Step("installed", timings, CollectUninstall);
            Step("app paths", timings, CollectAppPaths);
            Step("running", timings, CollectRunning);
            Step("packaged", timings, CollectPackaged);
            Report();
            WriteLog(timings, "quick");
        }

        /// <summary>Slow disk / game library discovery; runs after the window is usable.</summary>
        public void BuildDisk(string[] scanRoots, int scanDepth)
        {
            var timings = new System.Collections.Generic.List<string>();
            Step("scan", timings, delegate { CollectPortable(scanRoots, scanDepth); });
            Step("steam", timings, CollectSteam);
            Report();
            WriteLog(timings, "disk");
        }

        private void WriteLog(System.Collections.Generic.List<string> timings, string phase)
        {
            try
            {
                timings.Add("total entries: " + Entries.Count);
                File.WriteAllLines(Path.Combine(Path.GetTempPath(), "NexusQuickAdd-index-" + phase + ".log"), timings.ToArray());
            }
            catch { }
        }

        private void Step(string name, System.Collections.Generic.List<string> timings, Action work)
        {
            var sw = Stopwatch.StartNew();
            try { work(); }
            catch (Exception ex) { Log(name + ": " + ex.Message); }
            sw.Stop();
            timings.Add(string.Format("{0}: {1} ms, {2} entries", name, sw.ElapsedMilliseconds, Entries.Count));
            Report();
        }

        private static void Log(string s) { Debug.WriteLine("[index] " + s); }

        private void Report()
        {
            Action<int> p = Progress;
            if (p != null) p(Entries.Count);
        }

        private void Add(Entry e)
        {
            if (e == null || string.IsNullOrEmpty(e.Target)) return;
            lock (_gate) { AddCore(e); }
        }

        private void AddCore(Entry e)
        {
            if (string.IsNullOrEmpty(e.Name)) e.Name = Path.GetFileNameWithoutExtension(e.Target);
            e.Name = e.Name.Trim();
            if (e.Name.Length == 0) return;
            if (string.IsNullOrEmpty(e.Initials)) e.Initials = Rank.Initials(e.Name);

            string targetName = Path.GetFileName(e.Target).ToLowerInvariant();
            if (targetName.Contains("unins") || targetName.Contains("uninstall") ||
                targetName.Contains("setup") || targetName.Contains("crashpad")) return;
            string label = Rank.Normalize(e.Name);
            if (label.StartsWith("卸载") || label.StartsWith("uninstall") || label.StartsWith("repair")) return;

            foreach (string[] row in AliasTable)
            {
                bool hit = e.Name.IndexOf(row[0], StringComparison.OrdinalIgnoreCase) >= 0 ||
                           Path.GetFileNameWithoutExtension(e.Target).IndexOf(row[0], StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) continue;
                for (int i = 1; i < row.Length; i++)
                {
                    string alias = row[i];
                    if (alias.Length == 0) continue;
                    if (string.Equals(alias, e.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!e.Aliases.Contains(alias)) e.Aliases.Add(alias);
                }
                break;
            }

            string targetKey = Rank.Normalize(e.Target);
            string fullKey = targetKey + "|" + Rank.Normalize(e.Args);
            if (_byKey.ContainsKey(fullKey)) return;

            Entry existing;
            if (e.Args.Length == 0 && _byTarget.TryGetValue(targetKey, out existing))
            {
                // same program under another name (e.g. 微信 / WeChat): keep it searchable
                if (!string.Equals(existing.Name, e.Name, StringComparison.Ordinal) &&
                    !existing.Aliases.Contains(e.Name) && existing.Aliases.Count < 4)
                {
                    existing.Aliases.Add(e.Name);
                }
                // one row per plain program: the better source wins
                if (e.Priority < existing.Priority)
                {
                    if (string.IsNullOrEmpty(e.WorkDir)) e.WorkDir = existing.WorkDir;
                    if (!string.Equals(existing.Name, e.Name, StringComparison.Ordinal) && !e.Aliases.Contains(existing.Name)) e.Aliases.Add(existing.Name);
                    foreach (string alias in existing.Aliases)
                        if (!e.Aliases.Contains(alias) && !string.Equals(alias, e.Name, StringComparison.Ordinal)) e.Aliases.Add(alias);
                    Entries.Remove(existing);
                    _byTarget[targetKey] = e;
                    _byKey.Remove(Rank.Normalize(existing.Target) + "|" + Rank.Normalize(existing.Args));
                    _byKey[fullKey] = e;
                    Entries.Add(e);
                }
                return;
            }

            _byKey[fullKey] = e;
            if (e.Args.Length == 0) _byTarget[targetKey] = e;
            _byName[Rank.Normalize(e.Name)] = e;
            Entries.Add(e);
        }

        public bool HasName(string name)
        {
            return _byName.ContainsKey(Rank.Normalize(name));
        }

        // ---------- start menu ----------

        private void CollectStartMenu()
        {
            var dirs = new List<string>();
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"));
            foreach (string dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (string lnk in SafeFiles(dir, "*.lnk"))
                {
                    string target, args, work;
                    try { Native.ReadShortcut(lnk, out target, out args, out work); }
                    catch { continue; }
                    if (string.IsNullOrEmpty(target)) continue;
                    if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsShellHost(target)) continue;
                    string ext = Path.GetExtension(target).ToLowerInvariant();
                    if (ext != ".exe" && ext != ".com" && ext != ".bat" && ext != ".cmd") continue;
                    if (!File.Exists(target)) continue;
                    var e = new Entry();
                    e.Name = CleanLnkName(Path.GetFileNameWithoutExtension(lnk));
                    e.Target = target;
                    e.Args = args ?? "";
                    e.WorkDir = work ?? "";
                    e.Source = "开始菜单";
                    e.Priority = 1;
                    Add(e);
                }
            }
        }

        private static bool IsShellHost(string target)
        {
            string name = Path.GetFileName(target).ToLowerInvariant();
            return name == "control.exe" || name == "rundll32.exe" || name == "mmc.exe" ||
                   name == "msiexec.exe" || name == "wscript.exe" || name == "cscript.exe" ||
                   name == "launchwinapp.exe" || name == "mshta.exe";
        }

        private static string CleanLnkName(string name)
        {
            string[] junk = { " - 快捷方式", " - shortcut", " 快捷方式", "快捷方式" };
            foreach (string j in junk)
                if (name.EndsWith(j, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - j.Length);
            return name.Trim();
        }

        // ---------- installed programs ----------

        private void CollectUninstall()
        {
            string[] roots =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (string sub in roots)
            {
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                        using (RegistryKey root = baseKey.OpenSubKey(sub))
                        {
                            if (root == null) continue;
                            foreach (string name in root.GetSubKeyNames())
                            {
                                try { AddUninstallEntry(root, name); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }
            }

            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"))
                {
                    if (root != null)
                        foreach (string name in root.GetSubKeyNames())
                        {
                            try { AddUninstallEntry(root, name); } catch { }
                        }
                }
            }
            catch { }
        }

        private void AddUninstallEntry(RegistryKey root, string subName)
        {
            using (RegistryKey k = root.OpenSubKey(subName))
            {
                if (k == null) return;
                string display = k.GetValue("DisplayName") as string;
                if (string.IsNullOrEmpty(display)) return;
                if (display.StartsWith("Update for", StringComparison.OrdinalIgnoreCase)) return;
                if (display.StartsWith("KB", StringComparison.OrdinalIgnoreCase)) return;

                object sysComponent = k.GetValue("SystemComponent");
                if (sysComponent != null && Convert.ToInt32(sysComponent) == 1) return;
                object parent = k.GetValue("ParentKeyName");
                if (parent != null && Convert.ToString(parent).Length > 0) return;
                string releaseType = k.GetValue("ReleaseType") as string;
                if (!string.IsNullOrEmpty(releaseType) && releaseType.IndexOf("Update", StringComparison.OrdinalIgnoreCase) >= 0) return;

                string icon = Convert.ToString(k.GetValue("DisplayIcon"));
                string installLocation = Convert.ToString(k.GetValue("InstallLocation"));

                string exe = ExeFromDisplayIcon(icon);
                if (exe == null)
                {
                    if (!string.IsNullOrEmpty(installLocation) && Directory.Exists(installLocation))
                        exe = FindMainExe(installLocation, display);
                }
                if (exe == null || !File.Exists(exe)) return;

                var e = new Entry();
                e.Name = display.Trim();
                e.Target = exe;
                e.WorkDir = Path.GetDirectoryName(exe);
                e.Source = "已安装";
                e.Priority = 2;
                Add(e);
            }
        }

        private static string ExeFromDisplayIcon(string icon)
        {
            if (string.IsNullOrEmpty(icon)) return null;
            string s = icon.Trim().Trim('"');
            int comma = s.LastIndexOf(',');
            if (comma > 0) s = s.Substring(0, comma).Trim().Trim('"');
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s)) return s;
            return null;
        }

        private static string FindMainExe(string dir, string displayName)
        {
            if (!Directory.Exists(dir)) return null;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.exe"); }
            catch { return null; }
            if (files.Length == 0) return null;

            string want = Rank.Normalize(displayName);
            string wantDir = Rank.Normalize(Path.GetFileName(dir.TrimEnd('\\')));
            string best = null;
            int bestScore = -1;
            foreach (string f in files)
            {
                string n = Rank.Normalize(Path.GetFileNameWithoutExtension(f));
                string lname = Path.GetFileName(f).ToLowerInvariant();
                bool bad = false;
                foreach (string b in ExeBlacklist)
                    if (lname.IndexOf(b, StringComparison.Ordinal) >= 0) { bad = true; break; }
                if (bad) continue;

                int score = 0;
                if (want.Length > 2 && n.Length > 1 && (want.IndexOf(n, StringComparison.Ordinal) >= 0 || n.IndexOf(want, StringComparison.Ordinal) >= 0)) score += 60;
                if (wantDir.Length > 2 && (n == wantDir || wantDir.IndexOf(n, StringComparison.Ordinal) >= 0)) score += 40;
                if (n.Contains("launcher")) score += 15;
                if (n.Contains("main")) score += 10;
                try { score += (int)Math.Min(20, new FileInfo(f).Length / 200000); } catch { }
                if (score > bestScore) { bestScore = score; best = f; }
            }
            return best ?? files[0];
        }

        // ---------- app paths ----------

        private void CollectAppPaths()
        {
            string sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
            var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
            var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };
            foreach (RegistryHive hive in hives)
                foreach (RegistryView view in views)
                {
                    try
                    {
                        using (RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view))
                        using (RegistryKey root = baseKey.OpenSubKey(sub))
                        {
                            if (root == null) continue;
                            foreach (string name in root.GetSubKeyNames())
                            {
                                using (RegistryKey k = root.OpenSubKey(name))
                                {
                                    if (k == null) continue;
                                    string path = Convert.ToString(k.GetValue(null)).Trim().Trim('"');
                                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                                    if (!File.Exists(path)) continue;
                                    var e = new Entry();
                                    string desc = FileDescription(path);
                                    e.Name = string.IsNullOrEmpty(desc) ? Path.GetFileNameWithoutExtension(path) : desc;
                                    e.Target = path;
                                    e.WorkDir = Path.GetDirectoryName(path);
                                    e.Source = "应用路径";
                                    e.Priority = 3;
                                    Add(e);
                                }
                            }
                        }
                    }
                    catch { }
                }
        }

        private static string FileDescription(string path)
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(vi.FileDescription)) return vi.FileDescription.Trim();
                if (!string.IsNullOrEmpty(vi.ProductName)) return vi.ProductName.Trim();
            }
            catch { }
            return null;
        }

        // ---------- packaged (UWP / Store) apps ----------

        private void CollectPackaged()
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null) return;
            object shell = Activator.CreateInstance(shellType);
            object ns = shellType.InvokeMember("Namespace", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
            if (ns == null) return;
            object items = ns.GetType().InvokeMember("Items", System.Reflection.BindingFlags.InvokeMethod, null, ns, null);
            if (items == null) return;
            int count = Convert.ToInt32(items.GetType().InvokeMember("Count", System.Reflection.BindingFlags.GetProperty, null, items, null));
            for (int i = 0; i < count; i++)
            {
                try
                {
                    object item = items.GetType().InvokeMember("Item", System.Reflection.BindingFlags.InvokeMethod, null, items, new object[] { i });
                    if (item == null) continue;
                    string name = Convert.ToString(item.GetType().InvokeMember("Name", System.Reflection.BindingFlags.GetProperty, null, item, null));
                    string path = Convert.ToString(item.GetType().InvokeMember("Path", System.Reflection.BindingFlags.GetProperty, null, item, null));
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.StartsWith("::", StringComparison.Ordinal)) continue;
                    if (path.IndexOf('\\') >= 0 || path.IndexOf(':') >= 0) continue;   // file shortcuts, not apps
                    if (path.StartsWith("Microsoft.AutoGenerated", StringComparison.OrdinalIgnoreCase)) continue;
                    string display = string.IsNullOrEmpty(name) ? path : name;
                    // already covered by a shortcut or an installed-program entry
                    if (HasName(display)) continue;

                    var e = new Entry();
                    e.Name = display;
                    e.Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                    e.Args = "shell:AppsFolder\\" + path;
                    e.WorkDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    e.Source = "商店应用";
                    e.Priority = 4;
                    Add(e);
                }
                catch { }
            }
        }

        // ---------- running programs ----------

        private void CollectRunning()
        {
            string windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (path.StartsWith(windir, StringComparison.OrdinalIgnoreCase)) continue;
                    string lower = path.ToLowerInvariant();
                    if (lower.Contains("\\plugin") || lower.Contains("\\runtime\\") || lower.Contains("\\temp\\") ||
                        lower.Contains("updater") || lower.Contains("\\helper")) continue;
                    if (!File.Exists(path)) continue;
                    var e = new Entry();
                    string desc = FileDescription(path);
                    e.Name = string.IsNullOrEmpty(desc) ? Path.GetFileNameWithoutExtension(path) : desc;
                    e.Target = path;
                    e.WorkDir = Path.GetDirectoryName(path);
                    e.Source = "运行中";
                    e.Priority = 6;
                    Add(e);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        // ---------- steam ----------

        private void CollectSteam()
        {
            string steam = null;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                    if (k != null) steam = Convert.ToString(k.GetValue("SteamPath"));
            }
            catch { }
            if (string.IsNullOrEmpty(steam))
            {
                try
                {
                    using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                        if (k != null) steam = Convert.ToString(k.GetValue("SteamExe"));
                }
                catch { }
            }
            if (string.IsNullOrEmpty(steam)) return;
            steam = steam.Replace('/', '\\').TrimEnd('\\');
            if (!Directory.Exists(steam)) return;

            var clock = Stopwatch.StartNew();
            var libraries = new List<string>();
            libraries.Add(steam);
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\');
                    if (Directory.Exists(p) && !libraries.Contains(p)) libraries.Add(p);
                }
            }

            foreach (string lib in libraries)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string acf in SafeFilesTop(apps, "appmanifest_*.acf"))
                {
                    if (clock.ElapsedMilliseconds > 1200) return;
                    try
                    {
                        string text = File.ReadAllText(acf);
                        string name = Match(text, "\"name\"\\s+\"([^\"]+)\"");
                        string inst = Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
                        if (string.IsNullOrEmpty(inst)) continue;
                        string dir = Path.Combine(apps, "common", inst);
                        if (!Directory.Exists(dir)) continue;
                        string exe = FindMainExe(dir, string.IsNullOrEmpty(name) ? inst : name);
                        if (exe == null) continue;
                        var e = new Entry();
                        e.Name = string.IsNullOrEmpty(name) ? inst : name;
                        e.Target = exe;
                        e.WorkDir = Path.GetDirectoryName(exe);
                        e.Source = "Steam";
                        e.Priority = 5;
                        Add(e);
                    }
                    catch { }
                }
            }
        }

        private static string Match(string text, string pattern)
        {
            Match m = Regex.Match(text, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---------- portable / disk scan ----------

        private void CollectPortable(string[] roots, int depth)
        {
            if (roots == null) return;
            var sw = Stopwatch.StartNew();
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                // breadth first: every top-level folder is covered before descending deeper
                var queue = new Queue<KeyValuePair<string, int>>();
                queue.Enqueue(new KeyValuePair<string, int>(root, 0));
                int scanned = 0;
                int versionBudget = 1200;
                while (queue.Count > 0)
                {
                    if (sw.ElapsedMilliseconds > 3500) return;
                    KeyValuePair<string, int> cur = queue.Dequeue();
                    string[] exes;
                    try { exes = Directory.GetFiles(cur.Key, "*.exe"); }
                    catch { exes = new string[0]; }
                    foreach (string f in exes)
                    {
                        if (++scanned > 2500) return;
                        if ((scanned & 0x0F) == 0)
                        {
                            Report();
                            if (sw.ElapsedMilliseconds > 3500) return;
                        }
                        string lname = Path.GetFileName(f).ToLowerInvariant();
                        bool bad = false;
                        foreach (string b in ExeBlacklist)
                            if (lname.IndexOf(b, StringComparison.Ordinal) >= 0) { bad = true; break; }
                        if (bad) continue;
                        if (_byTarget.ContainsKey(Rank.Normalize(f))) continue;
                        if (Regex.IsMatch(Path.GetFileNameWithoutExtension(f), "^[0-9._-]+$")) continue;

                        long size = 0;
                        try { size = new FileInfo(f).Length; } catch { }
                        if (size < 150 * 1024) continue;

                        // reading version info is the expensive part: spend it only on real candidates
                        string desc = null;
                        if (versionBudget > 0) { versionBudget--; desc = FileDescription(f); }
                        if (desc == null && size < 400 * 1024) continue;

                        var e = new Entry();
                        e.Name = string.IsNullOrEmpty(desc) ? Path.GetFileNameWithoutExtension(f) : desc;
                        e.Target = f;
                        e.WorkDir = Path.GetDirectoryName(f);
                        e.Source = "磁盘";
                        e.Priority = 7;
                        Add(e);
                    }
                    if (cur.Value + 1 >= depth) continue;
                    string[] subDirs;
                    try { subDirs = Directory.GetDirectories(cur.Key); }
                    catch { continue; }
                    foreach (string d in subDirs)
                    {
                        string name = Path.GetFileName(d).ToLowerInvariant();
                        bool skip = false;
                        foreach (string b in DirBlacklist)
                            if (name.IndexOf(b, StringComparison.Ordinal) >= 0) { skip = true; break; }
                        if (!skip) queue.Enqueue(new KeyValuePair<string, int>(d, cur.Value + 1));
                    }
                }
            }
        }

        // ---------- helpers ----------

        private static IEnumerable<string> SafeFilesTop(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly); }
            catch { return new string[0]; }
        }

        private static IEnumerable<string> SafeFiles(string dir, string pattern)
        {
            try { return Directory.GetFiles(dir, pattern, SearchOption.AllDirectories); }
            catch { return new string[0]; }
        }
    }
}

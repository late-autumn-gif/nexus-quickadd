using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickAdd
{
    /// <summary>Win32 / COM interop helpers.</summary>
    internal static class Native
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const int WM_HOTKEY = 0x0312;

        // ---- shell shortcuts (WScript.Shell keeps the format correct without a parser) ----

        private static object NewShell()
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) throw new InvalidOperationException("WScript.Shell unavailable");
            return Activator.CreateInstance(t);
        }

        public static void CreateShortcut(string lnkPath, string target, string args, string workDir, string iconPath)
        {
            object shell = NewShell();
            object sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type t = sc.GetType();
            t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
            t.InvokeMember("Arguments", BindingFlags.SetProperty, null, sc, new object[] { args ?? "" });
            t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir ?? "" });
            if (!string.IsNullOrEmpty(iconPath))
                t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { iconPath });
            t.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }

        public static void ReadShortcut(string lnkPath, out string target, out string args, out string workDir)
        {
            target = ""; args = ""; workDir = "";
            object shell = NewShell();
            object sc = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            Type t = sc.GetType();
            target = Convert.ToString(t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null));
            args = Convert.ToString(t.InvokeMember("Arguments", BindingFlags.GetProperty, null, sc, null));
            workDir = Convert.ToString(t.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, sc, null));
        }

        // ---- icons ----

        private static readonly System.Collections.Generic.Dictionary<string, ImageSource> IconCache =
            new System.Collections.Generic.Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource LoadIcon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            ImageSource cached;
            if (IconCache.TryGetValue(path, out cached)) return cached;

            ImageSource result = null;
            try
            {
                if (File.Exists(path))
                {
                    using (Icon ico = Icon.ExtractAssociatedIcon(path))
                    {
                        if (ico != null)
                        {
                            BitmapSource bmp = Imaging.CreateBitmapSourceFromHIcon(
                                ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bmp.Freeze();
                            result = bmp;
                        }
                    }
                }
            }
            catch { }

            IconCache[path] = result;
            return result;
        }
    }
}

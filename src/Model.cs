using System;
using System.Collections.Generic;
using System.Text;

namespace QuickAdd
{
    /// <summary>One launchable program found on this machine.</summary>
    public class Entry
    {
        public string Name;        // label shown in the list
        public string Target;      // exe to launch (explorer.exe for packaged apps)
        public string Args = "";   // command line arguments
        public string WorkDir = "";// "start in" folder
        public string Source = ""; // where the entry came from
        public string Initials = "";// pinyin initials of Name
        public int Priority;       // lower is a better source
        public string Key = "";    // de-duplication key
        public readonly List<string> Aliases = new List<string>();  // other names for the same program

        public string ExeName
        {
            get
            {
                try { return System.IO.Path.GetFileName(Target) ?? ""; }
                catch { return ""; }
            }
        }

        public string Dir
        {
            get
            {
                if (!string.IsNullOrEmpty(WorkDir)) return WorkDir;
                try { return System.IO.Path.GetDirectoryName(Target) ?? ""; }
                catch { return ""; }
            }
        }

        public string SubLine
        {
            get
            {
                string p = Target;
                if (!string.IsNullOrEmpty(Args)) p = p + "  " + Args;
                return p;
            }
        }
    }

    /// <summary>Fuzzy-ish ranking tuned for program names in Chinese and English.</summary>
    public static class Rank
    {
        private static readonly Encoding Gbk = Encoding.GetEncoding(936);

        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>First letter of each Han character (GB2312 range table) plus latin letters.</summary>
        public static string Initials(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 0x4E00 && c <= 0x9FA5)
                {
                    byte[] b = Gbk.GetBytes(c.ToString());
                    if (b.Length == 2)
                    {
                        int code = b[0] * 256 + b[1];
                        sb.Append(LetterOf(code));
                        continue;
                    }
                }
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static char LetterOf(int code)
        {
            if (code >= 0xB0A1 && code <= 0xB0C4) return 'a';
            if (code <= 0xB2C0) return 'b';
            if (code <= 0xB4ED) return 'c';
            if (code <= 0xB6E9) return 'd';
            if (code <= 0xB7A1) return 'e';
            if (code <= 0xB8C0) return 'f';
            if (code <= 0xB9FD) return 'g';
            if (code <= 0xBBF6) return 'h';
            if (code <= 0xBFA5) return 'j';
            if (code <= 0xC0AB) return 'k';
            if (code <= 0xC2E7) return 'l';
            if (code <= 0xC4C2) return 'm';
            if (code <= 0xC5B5) return 'n';
            if (code <= 0xC5BD) return 'o';
            if (code <= 0xC6D9) return 'p';
            if (code <= 0xC8BA) return 'q';
            if (code <= 0xC8F5) return 'r';
            if (code <= 0xCBF9) return 's';
            if (code <= 0xCDD9) return 't';
            if (code <= 0xCEF3) return 'w';
            if (code <= 0xD188) return 'x';
            if (code <= 0xD4D0) return 'y';
            if (code <= 0xD7F9) return 'z';
            return ' ';
        }

        /// <summary>Score one entry against the normalized query; 0 means no match.</summary>
        public static int Score(Entry e, string q)
        {
            if (q.Length == 0) return 1;
            string name = Normalize(e.Name);
            string file = Normalize(e.ExeName);
            string path = Normalize(e.Target + " " + e.Args);
            string ini = e.Initials;

            int score = 0;
            foreach (string alias in e.Aliases)
            {
                string an = Normalize(alias);
                if (an.Length == 0) continue;
                if (an == q) score = Math.Max(score, 980);
                else if (an.StartsWith(q)) score = Math.Max(score, 700);
                else if (an.IndexOf(q, StringComparison.Ordinal) >= 0) score = Math.Max(score, 470);
            }
            if (name == q) score = 1000;
            else if (name.StartsWith(q)) score = 760;
            else if (ini == q) score = 700;
            else if (WordStarts(name, q)) score = 620;
            else if (ini.StartsWith(q)) score = 560;
            else if (name.IndexOf(q, StringComparison.Ordinal) >= 0) score = 520;
            else if (file.StartsWith(q)) score = 500;
            else if (ini.IndexOf(q, StringComparison.Ordinal) >= 0) score = 460;
            else if (file.IndexOf(q, StringComparison.Ordinal) >= 0) score = 380;
            else if (path.IndexOf(q, StringComparison.Ordinal) >= 0) score = 240;
            else if (q.Length >= 3 && Subsequence(file, q)) score = 110;

            if (score == 0) return 0;
            // shorter names win ties; higher-priority sources win ties
            score -= Math.Min(name.Length, 40);
            score += Math.Max(0, 8 - e.Priority);
            return score;
        }

        private static bool WordStarts(string name, string q)
        {
            for (int i = 0; i < name.Length; i++)
            {
                if (i == 0 || !char.IsLetterOrDigit(name[i - 1]))
                {
                    if (name.Length - i >= q.Length &&
                        string.CompareOrdinal(name, i, q, 0, q.Length) == 0) return true;
                }
            }
            return false;
        }

        private static bool Subsequence(string haystack, string needle)
        {
            int j = 0;
            for (int i = 0; i < haystack.Length && j < needle.Length; i++)
                if (haystack[i] == needle[j]) j++;
            return j == needle.Length;
        }

        public static List<Entry> Top(IEnumerable<Entry> entries, string query, int max)
        {
            string q = Normalize(query);
            var scored = new List<KeyValuePair<int, Entry>>();
            foreach (Entry e in entries)
            {
                int s = Score(e, q);
                if (s > 0) scored.Add(new KeyValuePair<int, Entry>(s, e));
            }
            scored.Sort(delegate (KeyValuePair<int, Entry> a, KeyValuePair<int, Entry> b)
            {
                if (a.Key != b.Key) return b.Key.CompareTo(a.Key);
                return string.Compare(a.Value.Name, b.Value.Name, StringComparison.CurrentCulture);
            });
            var result = new List<Entry>(Math.Min(max, scored.Count));
            for (int i = 0; i < scored.Count && i < max; i++) result.Add(scored[i].Value);
            return result;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace QuickAdd
{
    public class Row : INotifyPropertyChanged
    {
        public Entry Entry;
        private string _stateText = "";
        private bool _hasState;

        public ImageSource Icon { get; set; }
        public string Name { get; set; }
        public string SubLine { get; set; }
        public string Source { get; set; }

        public string StateText
        {
            get { return _stateText; }
            set { _stateText = value; Raise("StateText"); }
        }

        public bool HasState
        {
            get { return _hasState; }
            set { _hasState = value; Raise("HasState"); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void Raise(string name)
        {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }
    }

    public class MainWindow : Window
    {
        private const int MaxRows = 80;
        private const int HotkeyId = 0x4E51;

        private Grid _root;
        private TextBox _search;
        private TextBlock _placeholder, _status, _footer, _empty, _busyText, _toastText, _reindex, _version;
        private ListBox _list;
        private Button _apply, _close, _install, _bannerClose;
        private Border _busy, _toast, _searchBorder, _banner;
        private TextBlock _bannerText;

        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<Entry> _pending = new List<Entry>();
        private HashSet<string> _dock = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _dockCount;

        private Indexer _indexer = new Indexer();
        private DispatcherTimer _debounce;
        private DispatcherTimer _toastTimer;
        private volatile bool _indexing;
        private volatile bool _scanning;
        private DateTime _lastLiveRefresh = DateTime.MinValue;
        private int _count;

        private Config _config;
        private uint _hotkeyMods;
        private uint _hotkeyKey;
        private string _hotkeyText;

        public MainWindow()
        {
            _config = Config.Load();
            NexusEnv.Initialize(_config);
            InitWindow();
            InitUi();
            LoadDockState();
            WireEvents();
            RefreshNexusState();
            BeginIndex();
        }

        // ---------------- setup ----------------

        private void InitWindow()
        {
            Title = AppInfo.Name;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            Width = 900;
            Height = 580;
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            try { Icon = Native.LoadIcon(Assembly.GetExecutingAssembly().Location); }
            catch { }
        }

        private void InitUi()
        {
            string xaml;
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("main.xaml"))
            {
                if (s == null) throw new InvalidOperationException("embedded ui/main.xaml missing");
                using (var reader = new StreamReader(s, Encoding.UTF8))
                    xaml = reader.ReadToEnd();
            }
            _root = (Grid)XamlReader.Parse(xaml);
            Content = _root;

            _search = (TextBox)Find("SearchBox");
            _placeholder = (TextBlock)Find("PlaceholderText");
            _status = (TextBlock)Find("StatusText");
            _footer = (TextBlock)Find("FooterText");
            _empty = (TextBlock)Find("EmptyHint");
            _busyText = (TextBlock)Find("BusyText");
            _toastText = (TextBlock)Find("ToastText");
            _reindex = (TextBlock)Find("ReindexLink");
            _version = (TextBlock)Find("VersionText");
            _list = (ListBox)Find("ResultList");
            _apply = (Button)Find("ApplyButton");
            _close = (Button)Find("CloseButton");
            _busy = (Border)Find("BusyOverlay");
            _toast = (Border)Find("ToastBorder");
            _searchBorder = (Border)Find("SearchBorder");
            _banner = (Border)Find("Banner");
            _bannerText = (TextBlock)Find("BannerText");
            _install = (Button)Find("InstallDockButton");
            _bannerClose = (Button)Find("BannerCloseButton");

            _version.Text = "v" + AppInfo.Version;
            _list.ItemsSource = _rows;

            _debounce = new DispatcherTimer();
            _debounce.Interval = TimeSpan.FromMilliseconds(110);
            _debounce.Tick += delegate { _debounce.Stop(); Refresh(); };

            _toastTimer = new DispatcherTimer();
            _toastTimer.Interval = TimeSpan.FromSeconds(2.2);
            _toastTimer.Tick += delegate { _toastTimer.Stop(); HideToast(); };

            _hotkeyText = _config.Hotkey;
            ParseHotkey(_config.Hotkey);
        }

        private void ParseHotkey(string text)
        {
            uint mods = Native.MOD_CONTROL | Native.MOD_ALT;
            uint key = 0x4E; // N
            foreach (string raw in text.Split('+'))
            {
                string part = raw.Trim().ToLowerInvariant();
                if (part == "ctrl" || part == "control") mods |= Native.MOD_CONTROL;
                else if (part == "alt") mods |= Native.MOD_ALT;
                else if (part == "shift") mods |= Native.MOD_SHIFT;
                else if (part.Length == 1) key = (uint)char.ToUpperInvariant(part[0]);
                else if (part.StartsWith("f") && part.Length <= 3)
                {
                    int n;
                    if (int.TryParse(part.Substring(1), out n) && n >= 1 && n <= 24) key = (uint)(0x70 + n - 1);
                }
            }
            if (mods != 0 && key != 0) { _hotkeyMods = mods; _hotkeyKey = key; }
        }

        private void LoadDockState()
        {
            try
            {
                _dock = NexusDock.DockKeys();
                _dockCount = NexusDock.ItemCount();
            }
            catch { _dock = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }

        private void WireEvents()
        {
            Loaded += delegate { AnimateIn(); FocusSearch(); };
            Activated += delegate { FocusSearch(); };
            SourceInitialized += OnSourceInitialized;
            Closed += delegate { UnregisterHotkey(); };

            HeaderDrag();

            _close.Click += delegate { Close(); };
            _apply.Click += delegate { ApplyPending(); };
            _install.Click += delegate { InstallToDock(); };
            _bannerClose.Click += delegate { DismissBanner(); };
            _reindex.MouseLeftButtonUp += delegate { if (!_indexing && !_scanning) BeginIndex(); };
            _list.MouseDoubleClick += delegate { AddSelected(); };
            _list.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(OnRowButtonClick));

            _search.TextChanged += delegate
            {
                _placeholder.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _debounce.Stop();
                _debounce.Start();
            };
            _search.PreviewKeyDown += OnSearchKeyDown;
            _search.GotKeyboardFocus += delegate { SetSearchFocusVisual(true); };
            _search.LostKeyboardFocus += delegate { SetSearchFocusVisual(false); };
        }

        private void HeaderDrag()
        {
            FrameworkElement header = (FrameworkElement)Find("HeaderBar");
            header.MouseLeftButtonDown += delegate (object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                {
                    try { DragMove(); } catch { }
                }
            };
        }

        // ---------------- hotkey ----------------

        private void OnSourceInitialized(object sender, EventArgs e)
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            HwndSource src = HwndSource.FromHwnd(hwnd);
            if (src != null) src.AddHook(WndProc);
            if (!Native.RegisterHotKey(hwnd, HotkeyId, _hotkeyMods, _hotkeyKey))
                _hotkeyText = "热键被其它程序占用";
        }

        private void UnregisterHotkey()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Native.UnregisterHotKey(hwnd, HotkeyId);
            }
            catch { }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
            {
                ShowAndFocus();
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void ShowAndFocus()
        {
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Show();
            Activate();
            Topmost = true;
            Topmost = false;
            FocusSearch();
        }

        // ---------------- nexus state ----------------

        private void RefreshNexusState()
        {
            if (!NexusEnv.Found)
            {
                _banner.Visibility = Visibility.Visible;
                _banner.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xF6, 0xE8));
                _banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xDD, 0xB3));
                _bannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x5B, 0x00));
                _bannerText.Text = "没有检测到 Winstep Nexus —— 请先安装 Nexus，再回来添加程序";
                _install.Visibility = Visibility.Collapsed;
                _bannerClose.Visibility = Visibility.Visible;
                return;
            }

            bool onDock = NexusDock.HasSelfItem();
            _config.DockInstalled = onDock;
            if (onDock || _config.BannerDismissed)
            {
                _banner.Visibility = Visibility.Collapsed;
                return;
            }

            _banner.Visibility = Visibility.Visible;
            _banner.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF8, 0xFF));
            _banner.BorderBrush = new SolidColorBrush(Color.FromRgb(0xD7, 0xE6, 0xFF));
            _bannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
            _bannerText.Text = "把「速添」固定到停靠栏，以后像 Nexus 原生功能一样打开";
            _install.Visibility = Visibility.Visible;
            _install.Content = "加入停靠栏";
            _bannerClose.Visibility = Visibility.Visible;
        }

        private void DismissBanner()
        {
            _config.BannerDismissed = true;
            _config.Save();
            _banner.Visibility = Visibility.Collapsed;
        }

        private void InstallToDock()
        {
            if (!NexusEnv.Found)
            {
                ShowToast("没有找到 Winstep Nexus 安装目录", true);
                return;
            }
            _busyText.Text = "正在把「速添」固定到停靠栏…";
            _busy.Visibility = Visibility.Visible;
            _install.IsEnabled = false;

            ThreadPool.QueueUserWorkItem(delegate
            {
                ApplyResult result;
                try { result = NexusDock.InstallSelf(); }
                catch (Exception ex)
                {
                    result = new ApplyResult();
                    result.Error = ex.Message;
                }
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _busy.Visibility = Visibility.Collapsed;
                    _install.IsEnabled = true;
                    if (!string.IsNullOrEmpty(result.Error))
                    {
                        ShowToast(result.Error, true);
                        return;
                    }
                    _config.DockInstalled = true;
                    _config.Save();
                    LoadDockState();
                    UpdateStatus();
                    RefreshNexusState();
                    ShowToast(result.Added > 0 ? "已固定到停靠栏，Nexus 已重启" : "停靠栏里已经有「速添」了", false);
                }));
            });
        }

        // ---------------- indexing ----------------

        private void BeginIndex()
        {
            _indexing = true;
            _scanning = false;
            _count = 0;
            _reindex.Text = "索引中…";
            _reindex.Foreground = new SolidColorBrush(Color.FromRgb(0xB6, 0xBE, 0xCC));
            UpdateStatus();

            _indexer = new Indexer();
            Indexer indexer = _indexer;
            string[] roots = _config.Roots;
            int depth = _config.Depth;

            indexer.Progress = delegate (int n)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _count = n;
                    UpdateStatus();
                    if (_scanning && (DateTime.Now - _lastLiveRefresh).TotalMilliseconds > 700)
                    {
                        _lastLiveRefresh = DateTime.Now;
                        AdoptSnapshot();
                        Refresh();
                    }
                }));
            };

            ThreadPool.QueueUserWorkItem(delegate
            {
                indexer.BuildQuick();
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _indexing = false;
                    _scanning = true;
                    AdoptSnapshot();
                    UpdateStatus();
                    Refresh();
                }));

                indexer.BuildDisk(roots, depth);
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    _scanning = false;
                    AdoptSnapshot();
                    UpdateStatus();
                    Refresh();
                    _reindex.Text = "重新索引";
                    _reindex.Foreground = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
                }));
            });
        }

        private void AdoptSnapshot()
        {
            List<Entry> snapshot = _indexer.Snapshot();
            _entries.Clear();
            _entries.AddRange(snapshot);
        }

        private void UpdateStatus()
        {
            if (!NexusEnv.Found)
            {
                _status.Text = string.Format("未连接 Nexus · 已索引 {0} 个程序", _entries.Count);
                return;
            }
            if (_indexing)
                _status.Text = string.Format("正在建立索引…  已发现 {0} 个程序", _count);
            else if (_scanning)
                _status.Text = string.Format("共 {0} 个程序 · 后台扫描磁盘中 · 停靠栏 {1} 项", _entries.Count, _dockCount);
            else
                _status.Text = string.Format("共 {0} 个程序 · 停靠栏已有 {1} 项 · {2}", _entries.Count, _dockCount, _hotkeyText);
        }

        // ---------------- search ----------------

        private void Refresh()
        {
            List<Entry> top = Rank.Top(_entries, _search.Text, MaxRows);
            _rows.Clear();
            foreach (Entry e in top) _rows.Add(MakeRow(e));
            _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_rows.Count > 0) _list.SelectedIndex = 0;
            UpdateFooter();
        }

        private Row MakeRow(Entry e)
        {
            var r = new Row();
            r.Entry = e;
            r.Name = e.Name;
            r.SubLine = e.SubLine;
            r.Source = e.Source;
            r.Icon = Native.LoadIcon(e.Target);
            if (_pending.Contains(e))
            {
                r.StateText = "待添加";
                r.HasState = true;
            }
            else if (IsOnDock(e))
            {
                r.StateText = "已在栏";
                r.HasState = true;
            }
            return r;
        }

        private bool IsOnDock(Entry e)
        {
            string key = NexusDock.Normalize(e.Target) + "|";
            if (e.Args.Length > 0) key += NexusDock.Normalize(e.Args);
            return _dock.Contains(key);
        }

        private void UpdateFooter()
        {
            int n = _pending.Count;
            _footer.Text = string.Format("待添加 {0} 项", n);
            _apply.Content = n > 0 ? string.Format("应用到 Nexus ({0})", n) : "应用到 Nexus";
            _apply.IsEnabled = n > 0 && NexusEnv.Found;
        }

        // ---------------- interaction ----------------

        private void OnSearchKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; }
            else if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; }
            else if (e.Key == Key.Enter)
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                AddSelected();
                if (ctrl) ApplyPending();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_search.Text.Length > 0) _search.Text = "";
                else Close();
                e.Handled = true;
            }
        }

        private void MoveSelection(int delta)
        {
            if (_rows.Count == 0) return;
            int i = _list.SelectedIndex + delta;
            if (i < 0) i = 0;
            if (i >= _rows.Count) i = _rows.Count - 1;
            _list.SelectedIndex = i;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void OnRowButtonClick(object sender, RoutedEventArgs e)
        {
            FrameworkElement fe = e.OriginalSource as FrameworkElement;
            if (fe == null) return;
            Row row = fe.DataContext as Row;
            if (row == null) return;
            string tag = fe.Tag as string;
            if (tag == "add") QueueRow(row);
            else if (tag == "dir") OpenFolder(row.Entry.Dir);
        }

        private void OpenFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                ShowToast("找不到启动目录：" + dir, true);
                return;
            }
            try { Process.Start("explorer.exe", "\"" + dir + "\""); }
            catch (Exception ex) { ShowToast("打开目录失败：" + ex.Message, true); }
        }

        private void AddSelected()
        {
            Row row = _list.SelectedItem as Row;
            if (row == null) return;
            QueueRow(row);
        }

        private void QueueRow(Row row)
        {
            Entry e = row.Entry;
            if (_pending.Contains(e))
            {
                _pending.Remove(e);
                row.StateText = IsOnDock(e) ? "已在栏" : "";
                row.HasState = row.StateText.Length > 0;
                UpdateFooter();
                ShowToast(string.Format("已移出队列：{0}", e.Name), false);
                return;
            }
            _pending.Add(e);
            row.StateText = "待添加";
            row.HasState = true;
            UpdateFooter();
            ShowToast(string.Format("已加入队列：{0}", e.Name), false);
        }

        // ---------------- apply ----------------

        private void ApplyPending()
        {
            if (_pending.Count == 0) return;
            if (!NexusEnv.Found)
            {
                ShowToast("没有找到 Winstep Nexus 安装目录", true);
                return;
            }
            var batch = new List<Entry>(_pending);
            _busyText.Text = string.Format("正在写入停靠栏并重启 Nexus…（{0} 项）", batch.Count);
            _busy.Visibility = Visibility.Visible;

            ThreadPool.QueueUserWorkItem(delegate
            {
                ApplyResult result;
                try { result = NexusDock.Apply(batch); }
                catch (Exception ex)
                {
                    result = new ApplyResult();
                    result.Error = ex.Message;
                }
                Dispatcher.BeginInvoke(new Action(delegate { OnApplied(result, batch); }));
            });
        }

        private void OnApplied(ApplyResult result, List<Entry> batch)
        {
            _busy.Visibility = Visibility.Collapsed;

            if (!string.IsNullOrEmpty(result.Error))
            {
                ShowToast("写入失败：" + result.Error, true);
                return;
            }

            _pending.RemoveAll(delegate (Entry e) { return batch.Contains(e); });
            LoadDockState();
            UpdateStatus();
            Refresh();

            string msg = string.Format("已添加 {0} 项", result.Added);
            if (result.Restarted)
                msg += result.Verified ? "，Nexus 已重启" : "，Nexus 正在重启";
            else
                msg += "，下次启动 Nexus 生效";
            ShowToast(msg, false);
        }

        // ---------------- toast / animation ----------------

        private void ShowToast(string text, bool error)
        {
            _toastText.Text = text;
            _toast.Background = new SolidColorBrush(error ? Color.FromRgb(0xB4, 0x23, 0x18) : Color.FromRgb(0x0F, 0x17, 0x2A));
            _toast.Opacity = 0;
            var tt = new TranslateTransform(0, 12);
            _toast.RenderTransform = tt;

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _toast.BeginAnimation(OpacityProperty, fade);

            var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(220));
            slide.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            tt.BeginAnimation(TranslateTransform.YProperty, slide);

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void HideToast()
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            _toast.BeginAnimation(OpacityProperty, fade);
        }

        private void AnimateIn()
        {
            _root.Opacity = 0;
            var scale = new ScaleTransform(0.985, 0.985);
            _root.RenderTransform = scale;
            _root.RenderTransformOrigin = new Point(0.5, 0.5);

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _root.BeginAnimation(OpacityProperty, fade);

            var grow = new DoubleAnimation(0.985, 1, TimeSpan.FromMilliseconds(240));
            grow.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        private void SetSearchFocusVisual(bool focused)
        {
            _searchBorder.BorderBrush = new SolidColorBrush(focused ? Color.FromRgb(0x93, 0xC5, 0xFD) : Color.FromRgb(0xE7, 0xEA, 0xF0));
            _searchBorder.Background = new SolidColorBrush(focused ? Colors.White : Color.FromRgb(0xF7, 0xF9, 0xFC));
        }

        private void FocusSearch()
        {
            _search.Focus();
            Keyboard.Focus(_search);
            _search.CaretIndex = _search.Text.Length;
        }

        // ---------------- helpers ----------------

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

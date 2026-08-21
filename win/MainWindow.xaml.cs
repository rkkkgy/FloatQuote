using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace FloatQuote;

public partial class MainWindow : Window
{
    public const double CollapsedW = 167, CollapsedH = 19;
    public const double ExpandedW = 460, ExpandedH = 400;
    public static readonly (double W, double H) MinCollapsed = (130, 17);
    public static readonly (double W, double H) MinExpanded = (360, 280);
    const double ResizeMargin = 6;

    public AppConfig Cfg { get; private set; }
    public List<string> Stocks { get; set; }
    public int Index { get; set; }
    public int RefreshSeconds { get; private set; }
    public int ChartRefreshSeconds { get; private set; }
    public string SwitchEffect { get; internal set; }
    public int AutoSwitchSeconds { get; set; }
    public int DisplayCount { get; private set; }
    public ChartKind ChartMode { get; private set; }
    public KlinePeriod KlinePeriod { get; private set; }
    public int KlineCount { get; private set; }

    internal readonly Dictionary<string, Quote> Quotes = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<string, (List<MinutePoint> Points, double? Prev)> MinuteCache = new(StringComparer.OrdinalIgnoreCase);
    internal readonly Dictionary<(string Code, KlinePeriod Period), List<KlineBar>> KlineCache = [];
    internal bool Pinned;
    internal bool Expanded;
    internal string? SelectedCode;
    internal bool Hovered;
    internal int? CarouselRow;
    internal string? ResizeEdge;
    internal bool MouseActive;
    internal readonly List<StockRow> Rows = [];
    internal readonly MinuteChart Chart = new();
    internal readonly Button BackBtn = new();
    readonly Dictionary<ChartKind, Button> _chartKindBtns = [];
    TextBlock? _periodHint;
    int _klineSeq;
    bool _klinePending;
    DateTime _lastKlineFetch;
    internal readonly AnimatedText NameLabel;
    internal readonly AnimatedText PriceLabel;
    internal readonly AnimatedText ChangeLabel;
    internal readonly Grid HeaderPanel = new() { Height = 17, Margin = new Thickness(0, 0, 0, 3) };
    internal readonly StackPanel RowsPanel = new();
    internal readonly Grid DetailPanel = new();
    internal readonly Dictionary<string, TextBlock> InfoLabels = [];
    internal WinForms.NotifyIcon? Tray;
    internal DateTime LastActivity = DateTime.Now;

    Point? _dragOffset;
    Rect? _resizeOrigin;
    Point? _resizeStart;
    Point? _pressLocal, _pressGlobal;
    string? _pressedRowCode;
    string? _trayIconKey;
    int _quoteSeq, _minuteSeq;
    bool _minutePending;
    int _minuteFail;
    DateTime _minuteRetryAt;
    DateTime _lastOfflineFetch;
    DateTime _lastMinuteFetch;
    double[] _collapsedSize = [CollapsedW, CollapsedH];
    double[] _expandedSize = [ExpandedW, ExpandedH];
    readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _tickTimer = new();
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    MenuItem? _pinAct, _topAct;

    public MainWindow()
    {
        InitializeComponent();
        Cfg = ConfigStore.Load();
        RefreshSeconds = Cfg.RefreshSeconds;
        ChartRefreshSeconds = Cfg.ChartRefreshSeconds;
        SwitchEffect = Cfg.SwitchEffect;
        AutoSwitchSeconds = Cfg.AutoSwitchSeconds;
        DisplayCount = Math.Clamp(Cfg.DisplayCount, 1, ConfigStore.MaxDisplayCount);
        ChartMode = Cfg.ChartKind == "kline" ? ChartKind.Kline : ChartKind.Minute;
        KlinePeriod = KlinePeriods.Parse(Cfg.KlinePeriod);
        KlineCount = Math.Clamp(Cfg.KlineCount, ConfigStore.MinKlineCount, ConfigStore.MaxKlineCount);
        Topmost = Cfg.AlwaysOnTop;
        Stocks = ConfigStore.VisibleCodes(Cfg);
        _collapsedSize = LoadSize(Cfg.SizeCollapsed, [CollapsedW, CollapsedH], MinCollapsed);
        _expandedSize = LoadSize(Cfg.SizeExpanded, [ExpandedW, ExpandedH], MinExpanded);
        if (IsLegacyExpandedSize(Cfg.SizeExpanded))
        {
            _expandedSize = [ExpandedW, ExpandedH];
            Cfg.SizeExpanded = [(int)ExpandedW, (int)ExpandedH];
            ConfigStore.Save(Cfg);
        }

        NameLabel = new AnimatedText(11, FontWeights.Bold, new SolidColorBrush(Theme.TextMain), 66);
        PriceLabel = new AnimatedText(12, FontWeights.Bold, new SolidColorBrush(Theme.TextMain));
        ChangeLabel = new AnimatedText(11, FontWeights.Bold, new SolidColorBrush(Theme.TextMain));

        BuildUi();
        BuildTray();

        if (Cfg.Pos is { Length: 2 })
        {
            Left = Cfg.Pos[0];
            Top = Cfg.Pos[1];
        }

        _collapseTimer.Tick += (_, _) => { _collapseTimer.Stop(); SetExpanded(false); };
        _tickTimer.Interval = TimeSpan.FromSeconds(RefreshSeconds);
        _tickTimer.Tick += (_, _) => OnTick();
        _tickTimer.Start();
        _idleTimer.Tick += (_, _) => CheckAutoSwitch();
        _idleTimer.Start();

        MouseEnter += (_, _) => OnEnter();
        MouseLeave += (_, _) => OnLeave();
        PreviewMouseLeftButtonDown += OnPreviewDown;
        PreviewMouseMove += OnPreviewMove;
        PreviewMouseLeftButtonUp += OnPreviewUp;
        MouseDoubleClick += (_, e) => { if (e.ChangedButton == MouseButton.Left) TogglePinned(); };
        PreviewMouseWheel += OnWheel;
        PreviewMouseRightButtonUp += OnContext;

        SourceInitialized += (_, _) => ApplyToolWindow();
        SizeChanged += (_, _) =>
            Clip = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 10, 10);
        Closed += (_, _) =>
        {
            SavePos();
            SaveSize();
            if (Tray is not null) { Tray.Visible = false; Tray.Dispose(); }
        };

        SetExpanded(false);
        _ = RefreshQuotesAsync();
        RefreshChart(force: true);
        UpdateRows();
    }

    public string CurrentCode() => SelectedCode ?? Stocks[Index];

    public List<string> VisibleStocks()
    {
        var n = DisplayCount;
        var start = Math.Min(Index, Math.Max(0, Stocks.Count - n));
        return Stocks.Skip(start).Take(n).ToList();
    }

    public void SetExpanded(bool expanded)
    {
        Expanded = expanded;
        ClearCarousel();
        DetailPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        HeaderPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        RowsPanel.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        Chrome.Background = new SolidColorBrush(expanded
            ? Color.FromRgb(30, 34, 45)
            : Color.FromArgb(245, 30, 34, 45));
        if (!expanded) SelectedCode = null;
        UpdateLayout();
        if (expanded)
        {
            Width = Math.Max(_expandedSize[0], MinExpanded.W);
            Height = Math.Max(_expandedSize[1], MinExpanded.H);
            ClampToWorkArea();
        }
        else
        {
            Width = Math.Max(_collapsedSize[0], MinCollapsed.W);
            Height = CollapsedMinH();
        }
    }

    public void TogglePinned()
    {
        Pinned = !Pinned;
        if (Pinned)
        {
            _collapseTimer.Stop();
            SetExpanded(true);
        }
        if (_pinAct is not null) _pinAct.IsChecked = Pinned;
    }

    public void ToggleVisible()
    {
        MarkActivity();
        if (IsVisible) Hide();
        else { Show(); Activate(); }
    }

    public void ToggleTop()
    {
        Topmost = !Topmost;
        Cfg.AlwaysOnTop = Topmost;
        ConfigStore.Save(Cfg);
        if (_topAct is not null) _topAct.IsChecked = Topmost;
    }

    public void SetRefresh(int secs)
    {
        RefreshSeconds = secs;
        Cfg.RefreshSeconds = secs;
        ConfigStore.Save(Cfg);
        _tickTimer.Interval = TimeSpan.FromSeconds(secs);
    }

    public void SetAutoSwitch(int secs)
    {
        AutoSwitchSeconds = secs;
        Cfg.AutoSwitchSeconds = secs;
        ConfigStore.Save(Cfg);
    }

    public void SetSwitchEffect(string key)
    {
        if (key is not ("off" or "fade" or "slide_h" or "slide_v" or "pulse")) return;
        SwitchEffect = key;
        Cfg.SwitchEffect = key;
        ConfigStore.Save(Cfg);
    }

    public void PromptDisplayCount()
    {
        var n = TextPrompt.AskInt(this, "显示数量", "同时显示几只（正整数）：", DisplayCount, 1, ConfigStore.MaxDisplayCount);
        if (n is int v) SetDisplayCount(v);
    }

    public void SetCategoryVisible(string name, bool visible)
    {
        var cat = Cfg.Categories.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (cat is null) return;
        if (!visible && !Cfg.Categories.Any(c => c != cat && c.Visible && c.Stocks.Count > 0))
            visible = true;
        cat.Visible = visible;
        ConfigStore.Save(Cfg);
        ApplyVisibleStocks();
    }

    void ApplyVisibleStocks()
    {
        var cur = Stocks.Count > 0 ? Stocks[Math.Clamp(Index, 0, Stocks.Count - 1)] : null;
        Stocks = ConfigStore.VisibleCodes(Cfg);
        var maxStart = Math.Max(0, Stocks.Count - EffectiveCount());
        var found = cur is null ? -1 : Stocks.FindIndex(c => c.Equals(cur, StringComparison.OrdinalIgnoreCase));
        Index = Math.Min(found >= 0 ? found : 0, maxStart);
        SelectedCode = null;
        ClearCarousel();
        UpdateRows();
        SetExpanded(Expanded);
        UpdateDisplay();
        _ = RefreshQuotesAsync();
        RefreshChart(force: true);
    }

    public void SetDisplayCount(int n)
    {
        n = Math.Clamp(n, 1, ConfigStore.MaxDisplayCount);
        if (n == DisplayCount) return;
        DisplayCount = n;
        Cfg.DisplayCount = n;
        ConfigStore.Save(Cfg);
        ClearCarousel();
        var maxStart = Math.Max(0, Stocks.Count - EffectiveCount());
        Index = Math.Min(Index, maxStart);
        UpdateRows();
        SetExpanded(Expanded);
        UpdateDisplay();
    }

    public void SetChartMode(ChartKind kind)
    {
        ChartMode = kind;
        Cfg.ChartKind = kind == ChartKind.Kline ? "kline" : "minute";
        ConfigStore.Save(Cfg);
        UpdateChartKindButtons();
        ApplyCurrentChart();
        RefreshChart(force: true);
    }

    public void SetKlinePeriod(KlinePeriod period)
    {
        KlinePeriod = period;
        Cfg.KlinePeriod = KlinePeriods.Key(period);
        Cfg.ChartKind = "kline";
        ChartMode = ChartKind.Kline;
        ConfigStore.Save(Cfg);
        UpdateChartKindButtons();
        ApplyCurrentChart();
        RefreshChart(force: true);
    }

        void CycleKlinePeriod(int delta)
    {
        var all = KlinePeriods.All;
        var i = Array.IndexOf(all, KlinePeriod);
        if (i < 0) i = Array.IndexOf(all, KlinePeriod.Day);
        var next = Math.Clamp(i + (delta > 0 ? -1 : 1), 0, all.Length - 1);
        if (next == i) return;
        SetKlinePeriod(all[next]);
    }

    public void SetKlineCount(int n)
    {
        n = Math.Clamp(n, ConfigStore.MinKlineCount, ConfigStore.MaxKlineCount);
        if (n == KlineCount) return;
        KlineCount = n;
        Cfg.KlineCount = n;
        ConfigStore.Save(Cfg);
        UpdateChartKindButtons();
        ApplyCurrentChart();
        var code = CurrentCode();
        if (!KlineCache.TryGetValue((code, KlinePeriod), out var bars) || bars.Count < n)
            RefreshChart(force: true);
    }

    public void PromptKlineCount()
    {
        var n = TextPrompt.AskInt(this, "K线根数",
            "展示多少根K线（从左往右排列，不够的留白）：",
            KlineCount, ConfigStore.MinKlineCount, ConfigStore.MaxKlineCount);
        if (n is int v) SetKlineCount(v);
    }

    void UpdateChartKindButtons()
    {
        foreach (var (kind, btn) in _chartKindBtns)
        {
            btn.Background = new SolidColorBrush(kind == ChartMode
                ? Color.FromRgb(62, 107, 79)
                : Color.FromRgb(44, 49, 64));
        }
        if (_periodHint is not null)
        {
            _periodHint.Visibility = ChartMode == ChartKind.Kline ? Visibility.Visible : Visibility.Collapsed;
            _periodHint.Text = $"当前 {KlinePeriods.Label(KlinePeriod)} · {KlineCount}根 · 滚轮切周期";
        }
    }

    void ApplyCurrentChart()
    {
        var code = CurrentCode();
        if (ChartMode == ChartKind.Minute)
        {
            if (MinuteCache.TryGetValue(code, out var cached))
                Chart.SetData(cached.Points, cached.Prev, QuoteClient.SessionOf(code));
            else
                Chart.SetLoading();
            return;
        }
        if (KlineCache.TryGetValue((code, KlinePeriod), out var bars) && bars.Count > 0)
            Chart.SetKline(bars, KlinePeriods.Label(KlinePeriod), KlineCount);
        else
            Chart.SetLoading();
    }

    void RefreshChart(bool force = false)
    {
        if (ChartMode == ChartKind.Minute)
        {
            _minutePending = false;
            _minuteRetryAt = DateTime.MinValue;
            _ = RefreshMinuteAsync(force);
        }
        else
        {
            _klinePending = false;
            _ = RefreshKlineAsync(force);
        }
    }

    public void SwitchTo(int i)
    {
        var maxStart = Math.Max(0, Stocks.Count - DisplayCount);
        var target = Math.Clamp(i, 0, maxStart);
        if (target == Index) return;
        ClearCarousel();
        Index = target;
        MarkActivity();
        UpdateDisplay();
        UpdateRows(animate: !Expanded);
        PlaySwitchAnimation();
        _ = RefreshQuotesAsync();
        ApplyCurrentChart();
        RefreshChart(force: true);
    }

    public void OnRowClick(string code)
    {
        SelectedCode = code;
        MarkActivity();
        _collapseTimer.Stop();
        SetExpanded(true);
        UpdateDisplay();
        ApplyCurrentChart();
        RefreshChart(force: true);
    }

    public void CollapseNow()
    {
        _collapseTimer.Stop();
        if (Pinned)
        {
            Pinned = false;
            if (_pinAct is not null) _pinAct.IsChecked = false;
        }
        SetExpanded(false);
    }

    internal void OnEnter()
    {
        Hovered = true;
        _collapseTimer.Stop();
        MarkActivity();
    }

    internal void OnLeave()
    {
        Hovered = false;
        MarkActivity();
        if (!Pinned && !MouseActive)
            _collapseTimer.Start();
    }

    internal string? EdgeAt(Point pos)
    {
        var x = pos.X; var y = pos.Y;
        var w = ActualWidth; var h = ActualHeight;
        var m = ResizeMargin;
        var left = x <= m; var right = x >= w - m;
        var top = y <= m; var bottom = y >= h - m;
        if (top && left) return "tl";
        if (top && right) return "tr";
        if (bottom && left) return "bl";
        if (bottom && right) return "br";
        if (left) return "l";
        if (right) return "r";
        if (top) return "t";
        if (bottom) return "b";
        return null;
    }

    internal ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item(IsVisible ? "隐藏悬浮窗" : "显示悬浮窗", ToggleVisible));

        var switchMenu = new MenuItem { Header = "切换股票" };
        for (var i = 0; i < Stocks.Count; i++)
        {
            var idx = i;
            var code = Stocks[i];
            Quotes.TryGetValue(code, out var q);
            var label = q is { Name: not null and not "" } ? $"{q.Name}  {code}" : code;
            var act = new MenuItem { Header = label, IsCheckable = true, IsChecked = i == Index };
            act.Click += (_, _) => SwitchTo(idx);
            switchMenu.Items.Add(act);
        }
        menu.Items.Add(switchMenu);
        menu.Items.Add(Item("编辑自选股…", EditStocks));

        if (Cfg.Categories.Count > 0)
        {
            var catMenu = new MenuItem { Header = "分类显示" };
            foreach (var cat in Cfg.Categories)
            {
                var name = cat.Name;
                var act = new MenuItem
                {
                    Header = $"{cat.Name}  ({cat.Stocks.Count})",
                    IsCheckable = true,
                    IsChecked = cat.Visible,
                };
                act.Click += (_, _) => SetCategoryVisible(name, act.IsChecked);
                catMenu.Items.Add(act);
            }
            menu.Items.Add(catMenu);
        }

        var refresh = new MenuItem { Header = "刷新间隔" };
        foreach (var secs in new[] { 2, 3, 5, 10 })
        {
            var s = secs;
            var act = new MenuItem { Header = $"{secs} 秒", IsCheckable = true, IsChecked = secs == RefreshSeconds };
            act.Click += (_, _) => SetRefresh(s);
            refresh.Items.Add(act);
        }
        menu.Items.Add(refresh);

        var auto = new MenuItem { Header = "自动轮播" };
        foreach (var (label, secs) in new (string, int)[] { ("关闭", 0), ("5 秒", 5), ("10 秒", 10), ("20 秒", 20), ("30 秒", 30) })
        {
            var s = secs;
            var act = new MenuItem { Header = label, IsCheckable = true, IsChecked = secs == AutoSwitchSeconds };
            act.Click += (_, _) => SetAutoSwitch(s);
            auto.Items.Add(act);
        }
        menu.Items.Add(auto);

        menu.Items.Add(Item($"显示数量 ({DisplayCount})…", PromptDisplayCount));

        var klineCount = new MenuItem { Header = $"K线根数 ({KlineCount})" };
        foreach (var n in new[] { 30, 40, 60, 80, 120 })
        {
            var nn = n;
            var act = new MenuItem { Header = $"{n} 根", IsCheckable = true, IsChecked = n == KlineCount };
            act.Click += (_, _) => SetKlineCount(nn);
            klineCount.Items.Add(act);
        }
        klineCount.Items.Add(new Separator());
        klineCount.Items.Add(Item("自定义…", PromptKlineCount));
        menu.Items.Add(klineCount);

        var anim = new MenuItem { Header = "切换动画" };
        foreach (var (label, key) in new (string, string)[] { ("关闭", "off"), ("淡入淡出", "fade"), ("左右滑入", "slide_h"), ("上下滚动", "slide_v"), ("脉冲闪烁", "pulse") })
        {
            var k = key;
            var act = new MenuItem { Header = label, IsCheckable = true, IsChecked = key == SwitchEffect };
            act.Click += (_, _) => SetSwitchEffect(k);
            anim.Items.Add(act);
        }
        menu.Items.Add(anim);

        _pinAct = new MenuItem { Header = "锁定展开", IsCheckable = true, IsChecked = Pinned };
        _pinAct.Click += (_, _) => TogglePinned();
        menu.Items.Add(_pinAct);

        _topAct = new MenuItem { Header = "窗口置顶", IsCheckable = true, IsChecked = Topmost };
        _topAct.Click += (_, _) => ToggleTop();
        menu.Items.Add(_topAct);

        menu.Items.Add(new Separator());
        menu.Items.Add(Item("退出", () => Application.Current.Shutdown()));
        return menu;
    }

    // ---- UI ----
    void BuildUi()
    {
        HeaderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HeaderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        HeaderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        HeaderPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(NameLabel, 0);
        Grid.SetColumn(PriceLabel, 2);
        Grid.SetColumn(ChangeLabel, 3);
        ChangeLabel.Margin = new Thickness(3, 0, 0, 0);
        HeaderPanel.Children.Add(NameLabel);
        HeaderPanel.Children.Add(PriceLabel);
        HeaderPanel.Children.Add(ChangeLabel);
        DockPanel.SetDock(HeaderPanel, Dock.Top);

        RowsPanel.Margin = new Thickness(0);
        DockPanel.SetDock(RowsPanel, Dock.Top);
        EnsureRows();

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        for (var r = 0; r < 4; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        var fields = new (string Title, string Key)[]
        {
            ("今开", "open"), ("昨收", "prev_close"), ("最高", "high"), ("最低", "low"),
            ("成交量", "volume"), ("成交额", "amount"), ("换手", "turnover"), ("市盈", "pe"),
        };
        for (var i = 0; i < fields.Length; i++)
        {
            var t = new TextBlock
            {
                Text = fields[i].Title,
                FontFamily = new FontFamily(Theme.UiFont),
                FontSize = 10,
                Foreground = new SolidColorBrush(Theme.TextSub),
                Margin = new Thickness(0, 0, 4, 0),
            };
            var v = new TextBlock
            {
                Text = "--",
                FontFamily = new FontFamily(Theme.MonoFont),
                FontSize = 10,
                Foreground = new SolidColorBrush(Theme.TextMain),
            };
            Grid.SetRow(t, i / 2);
            Grid.SetColumn(t, (i % 2) * 2);
            Grid.SetRow(v, i / 2);
            Grid.SetColumn(v, (i % 2) * 2 + 1);
            grid.Children.Add(t);
            grid.Children.Add(v);
            InfoLabels[fields[i].Key] = v;
        }

        BackBtn.Content = "返回";
        BackBtn.Style = (Style)FindResource("PillButton");
        BackBtn.Width = 56;
        BackBtn.Cursor = Cursors.Hand;
        BackBtn.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        BackBtn.Background = new SolidColorBrush(Color.FromRgb(44, 49, 64));
        BackBtn.Foreground = new SolidColorBrush(Theme.TextMain);
        BackBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 65, 80));
        BackBtn.Click += (_, _) => CollapseNow();

        var kindBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 2),
        };
        foreach (var (label, kind) in new (string, ChartKind)[] { ("分时", ChartKind.Minute), ("K线", ChartKind.Kline) })
        {
            var btn = new Button
            {
                Content = label,
                Style = (Style)FindResource("PillButton"),
                Width = 48,
                Margin = new Thickness(3, 0, 3, 0),
                Cursor = Cursors.Hand,
                Foreground = new SolidColorBrush(Theme.TextMain),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 65, 80)),
            };
            var k = kind;
            btn.Click += (_, _) => SetChartMode(k);
            _chartKindBtns[kind] = btn;
            kindBar.Children.Add(btn);
        }
        _periodHint = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Theme.TextSub),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        kindBar.Children.Add(_periodHint);
        UpdateChartKindButtons();

        DetailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        DetailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        DetailPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        DetailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(grid, 0);
        Grid.SetRow(kindBar, 1);
        Grid.SetRow(Chart, 2);
        Grid.SetRow(BackBtn, 3);
        BackBtn.Margin = new Thickness(0, 4, 0, 2);
        DetailPanel.Children.Add(grid);
        DetailPanel.Children.Add(kindBar);
        DetailPanel.Children.Add(Chart);
        DetailPanel.Children.Add(BackBtn);

        Root.Children.Add(HeaderPanel);
        Root.Children.Add(RowsPanel);
        Root.Children.Add(DetailPanel);
    }

    int EffectiveCount() => Math.Max(1, Math.Min(DisplayCount, Stocks.Count));

    double CollapsedMinH()
    {
        var n = EffectiveCount();
        return n * StockRow.RowH + (n - 1) * 1 + 2;
    }

    void EnsureRows()
    {
        var target = EffectiveCount();
        while (Rows.Count < target)
        {
            var row = new StockRow();
            RowsPanel.Children.Add(row);
            Rows.Add(row);
        }
        while (Rows.Count > target)
        {
            var last = Rows[^1];
            Rows.RemoveAt(Rows.Count - 1);
            RowsPanel.Children.Remove(last);
        }
        RelayoutRowMargins();
    }

    void RelayoutRowMargins()
    {
        for (var i = 0; i < Rows.Count; i++)
            Rows[i].Margin = new Thickness(0, 0, 0, i < Rows.Count - 1 ? 1 : 0);
    }

    void UpdateRows(bool animate = false)
    {
        EnsureRows();
        var codes = VisibleStocks();
        for (var i = 0; i < Rows.Count; i++)
        {
            var row = Rows[i];
            if (i < codes.Count)
            {
                var code = codes[i];
                if (Quotes.TryGetValue(code, out var q))
                {
                    var brush = Theme.BrushOf(q.ColorKey);
                    var arrow = q.ColorKey == "red" ? "▲" : q.ColorKey == "green" ? "▼" : "—";
                    row.SetStock(code, q.Name, QuoteClient.FmtPrice(q.Price),
                        $"{arrow} {QuoteClient.FmtPct(q.ChangePct)}", brush);
                }
                else
                {
                    row.SetStock(code, code, "--", "--", new SolidColorBrush(Theme.TextSub));
                }
                if (animate) row.Play(SwitchEffect);
            }
            else row.Clear();
        }
    }

    void UpdateDisplay()
    {
        if (!Quotes.TryGetValue(CurrentCode(), out var q)) return;
        var brush = Theme.BrushOf(q.ColorKey);
        var arrow = q.ColorKey == "red" ? "▲" : q.ColorKey == "green" ? "▼" : "—";
        NameLabel.SetText(q.Name);
        PriceLabel.SetText(QuoteClient.FmtPrice(q.Price), brush);
        ChangeLabel.SetText($"{arrow} {QuoteClient.FmtPct(q.ChangePct)}", brush);

        InfoLabels["open"].Text = QuoteClient.FmtPrice(q.Open);
        InfoLabels["prev_close"].Text = QuoteClient.FmtPrice(q.PrevClose);
        InfoLabels["high"].Text = QuoteClient.FmtPrice(q.High);
        InfoLabels["low"].Text = QuoteClient.FmtPrice(q.Low);
        InfoLabels["volume"].Text = QuoteClient.FmtVolume(q.Volume);
        InfoLabels["amount"].Text = QuoteClient.FmtAmount(q.Amount);
        InfoLabels["turnover"].Text = q.Turnover is null ? "--" : QuoteClient.FmtPct(q.Turnover);
        InfoLabels["pe"].Text = q.Pe is null ? "--" : $"{q.Pe:0.00}";

        if (Tray is not null)
        {
            var tip = $"{q.Name}  {QuoteClient.FmtPrice(q.Price)}  ({QuoteClient.FmtPct(q.ChangePct)})";
            Tray.Text = tip.Length <= 63 ? tip : tip[..63];
            if (q.ColorKey != _trayIconKey)
            {
                _trayIconKey = q.ColorKey;
                var old = Tray.Icon;
                Tray.Icon = TrayIconFactory.Make(q.ColorKey);
                old?.Dispose();
            }
        }
    }

    void PlaySwitchAnimation()
    {
        if (!IsVisible || !Expanded) return;
        NameLabel.Play(SwitchEffect, -1);
        PriceLabel.Play(SwitchEffect, 1);
        ChangeLabel.Play(SwitchEffect, 1);
    }

    // ---- data ----
    void OnTick()
    {
        var now = DateTime.Now;
        if (QuoteClient.ShouldRefresh(Stocks, now))
        {
            _ = RefreshQuotesAsync();
            if (ChartMode == ChartKind.Minute)
            {
                if (!MinuteCache.ContainsKey(CurrentCode())
                    || (now - _lastMinuteFetch).TotalSeconds >= ChartRefreshSeconds)
                    _ = RefreshMinuteAsync();
            }
            else if (!KlineCache.ContainsKey((CurrentCode(), KlinePeriod))
                     || (now - _lastKlineFetch).TotalSeconds >= Math.Max(60, ChartRefreshSeconds))
            {
                _ = RefreshKlineAsync();
            }
        }
        else if ((now - _lastOfflineFetch).TotalSeconds > 300)
        {
            _lastOfflineFetch = now;
            _ = RefreshQuotesAsync();
        }
    }

    async Task RefreshQuotesAsync()
    {
        var seq = ++_quoteSeq;
        try
        {
            var qs = await QuoteClient.FetchQuotesAsync(Stocks).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (seq != _quoteSeq) return;
                foreach (var kv in qs) Quotes[kv.Key] = kv.Value;
                UpdateDisplay();
                UpdateRows();
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FloatQuote] 报价刷新失败: {ex.Message}");
        }
    }

    async Task RefreshMinuteAsync(bool force = false)
    {
        if (_minutePending) return;
        if (!force && DateTime.Now < _minuteRetryAt) return;
        _minutePending = true;
        _lastMinuteFetch = DateTime.Now;
        var seq = ++_minuteSeq;
        var code = CurrentCode();
        try
        {
            var (pts, _) = await QuoteClient.FetchMinuteAsync(code).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (seq != _minuteSeq) return;
                _minutePending = false;
                _minuteFail = 0;
                _minuteRetryAt = DateTime.MinValue;
                Quotes.TryGetValue(code, out var q);
                MinuteCache[code] = (pts, q?.PrevClose);
                if (code == CurrentCode() && ChartMode == ChartKind.Minute)
                    Chart.SetData(pts, q?.PrevClose, QuoteClient.SessionOf(code));
            });
        }
        catch (Exception first)
        {
            try
            {
                var img = await QuoteClient.FetchMinuteImageAsync(code).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (seq != _minuteSeq) return;
                    _minutePending = false;
                    ApplyMinuteBackoff();
                    if (img is { Length: > 0 } && code == CurrentCode() && ChartMode == ChartKind.Minute)
                        Chart.ShowImage(img);
                });
            }
            catch (Exception second)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (seq != _minuteSeq) return;
                    _minutePending = false;
                    ApplyMinuteBackoff();
                    Debug.WriteLine($"[FloatQuote] 分时数据失败: {second.Message} / {first.Message}");
                });
            }
        }
    }

    async Task RefreshKlineAsync(bool force = false)
    {
        if (ChartMode == ChartKind.Minute) return;
        if (_klinePending) return;
        _klinePending = true;
        _lastKlineFetch = DateTime.Now;
        var seq = ++_klineSeq;
        var code = CurrentCode();
        var period = KlinePeriod;
        var label = KlinePeriods.Label(period);
        try
        {
            var bars = await QuoteClient.FetchKlineAsync(code, period, KlineCount).ConfigureAwait(false);
            await Dispatcher.InvokeAsync(() =>
            {
                if (seq != _klineSeq) return;
                _klinePending = false;
                KlineCache[(code, period)] = bars;
                if (code == CurrentCode() && ChartMode == ChartKind.Kline && KlinePeriod == period)
                    Chart.SetKline(bars, label, KlineCount);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (seq != _klineSeq) return;
                _klinePending = false;
                if (code == CurrentCode() && ChartMode == ChartKind.Kline && KlinePeriod == period)
                    Chart.SetKline([], label, KlineCount);
                Debug.WriteLine($"[FloatQuote] K线失败: {ex.Message}");
            });
        }
    }

    void ApplyMinuteBackoff()
    {
        _minuteFail++;
        var delay = Math.Min(120, 15 * Math.Pow(2, _minuteFail - 1));
        _minuteRetryAt = DateTime.Now.AddSeconds(delay);
    }

    void CheckAutoSwitch()
    {
        if (AutoSwitchSeconds <= 0 || Stocks.Count < 2) return;
        if (!IsVisible || Expanded || Hovered) return;
        if ((DateTime.Now - LastActivity).TotalSeconds < AutoSwitchSeconds) return;
        var maxStart = Math.Max(0, Stocks.Count - DisplayCount);
        if (maxStart > 0)
            SwitchTo((Index + 1) % (maxStart + 1));
        else
        {
            SetCarouselHighlight(CarouselRow is null ? 0 : CarouselRow.Value + 1);
            MarkActivity();
        }
    }

    void SetCarouselHighlight(int row)
    {
        if (Rows.Count == 0) return;
        row %= Rows.Count;
        if (CarouselRow == row) return;
        if (CarouselRow is int old && old >= 0 && old < Rows.Count)
            Rows[old].SetHighlight(false);
        CarouselRow = row;
        Rows[row].SetHighlight(true);
    }

    void ClearCarousel()
    {
        if (CarouselRow is int r && r >= 0 && r < Rows.Count)
            Rows[r].SetHighlight(false);
        CarouselRow = null;
    }

    void MarkActivity() => LastActivity = DateTime.Now;

    void EditStocks()
    {
        var names = Quotes.Where(kv => kv.Value.Name is not ("--" or ""))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Name, StringComparer.OrdinalIgnoreCase);
        var all = ConfigStore.AllCodes(Cfg);
        var dlg = new StockEditDialog(all, names, Cfg.Categories) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var cats = dlg.Categories();
        var codes = dlg.Codes();
        if (codes.Count == 0) return;
        Cfg.Categories = cats;
        Cfg.Stocks = codes;
        ConfigStore.EnsureCategories(Cfg);
        ConfigStore.Save(Cfg);
        foreach (var code in codes)
        {
            if (!Quotes.ContainsKey(code) && dlg.NameOf(code) is { } n)
                Quotes[code] = new Quote { Code = code, Name = n };
        }
        ApplyVisibleStocks();
    }

    // ---- mouse ----
    string? RowAt(Point pos)
    {
        foreach (var row in Rows)
        {
            if (row.Code is null || row.Visibility != Visibility.Visible) continue;
            var p = row.TranslatePoint(new Point(0, 0), this);
            var rect = new Rect(p, new Size(row.ActualWidth, row.ActualHeight));
            if (rect.Contains(pos)) return row.Code;
        }
        return null;
    }

    void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null)
            return;
        ClearCarousel();
        MarkActivity();
        MouseActive = true;
        var local = e.GetPosition(this);
        _pressLocal = local;
        _pressGlobal = PointToScreen(local);
        _pressedRowCode = Expanded ? null : RowAt(local);
        var edge = EdgeAt(local);
        if (edge is not null)
        {
            ResizeEdge = edge;
            _resizeOrigin = new Rect(Left, Top, Width, Height);
            _resizeStart = PointToScreen(local);
            CaptureMouse();
        }
        else
        {
            var screen = PointToScreen(local);
            _dragOffset = new Point(screen.X - Left, screen.Y - Top);
            CaptureMouse();
        }
    }

    void OnPreviewMove(object sender, MouseEventArgs e)
    {
        MarkActivity();
        var local = e.GetPosition(this);
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            if (ResizeEdge is not null && _resizeStart is not null && _resizeOrigin is not null)
            {
                DoResize(PointToScreen(local));
                return;
            }
            if (_dragOffset is Point off)
            {
                var screen = PointToScreen(local);
                Left = screen.X - off.X;
                Top = screen.Y - off.Y;
            }
        }
        else
        {
            if (Expanded && OverChart(local))
                Cursor = Cursors.Cross;
            else
                Cursor = CursorFor(EdgeAt(local));
        }
    }

    internal void ResizeBy(string edge, double dx, double dy)
    {
        ResizeEdge = edge;
        _resizeOrigin = new Rect(Left, Top, Width, Height);
        _resizeStart = new Point(0, 0);
        DoResize(new Point(dx, dy));
        ResizeEdge = null;
        _resizeOrigin = null;
        _resizeStart = null;
        SaveSize();
        SavePos();
    }

    void DoResize(Point gpos)
    {
        if (ResizeEdge is null || _resizeOrigin is null || _resizeStart is null) return;
        var geo = _resizeOrigin.Value;
        var edge = ResizeEdge;
        var x = geo.X; var y = geo.Y; var w = geo.Width; var h = geo.Height;
        var dx = gpos.X - _resizeStart.Value.X;
        var dy = gpos.Y - _resizeStart.Value.Y;
        var minW = Expanded ? MinExpanded.W : MinCollapsed.W;
        var minH = Expanded ? MinExpanded.H : MinCollapsed.H;
        if (edge.Contains('l'))
        {
            var nw = w - dx;
            if (nw >= minW) { x = geo.X + dx; w = nw; }
        }
        if (edge.Contains('r')) w = Math.Max(minW, w + dx);
        if (edge.Contains('t'))
        {
            var nh = h - dy;
            if (nh >= minH) { y = geo.Y + dy; h = nh; }
        }
        if (edge.Contains('b')) h = Math.Max(minH, h + dy);
        Left = x; Top = y; Width = w; Height = h;
    }

    void OnPreviewUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        MouseActive = false;
        ReleaseMouseCapture();
        var moved = 9999.0;
        if (_pressGlobal is Point pg)
        {
            var now = PointToScreen(e.GetPosition(this));
            moved = Math.Abs(now.X - pg.X) + Math.Abs(now.Y - pg.Y);
        }
        _pressLocal = null;
        _pressGlobal = null;
        if (ResizeEdge is not null)
        {
            ResizeEdge = null;
            _resizeOrigin = null;
            _resizeStart = null;
            SaveSize();
            SavePos();
            return;
        }
        if (_dragOffset is not null)
        {
            _dragOffset = null;
            SavePos();
            if (moved <= 4 && !Expanded)
            {
                var code = _pressedRowCode ?? Stocks[Index];
                OnRowClick(code);
            }
        }
        _pressedRowCode = null;
    }

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        MarkActivity();
        e.Handled = true;
        if (Expanded && OverChart(e.GetPosition(this)))
        {
            if (ChartMode == ChartKind.Kline)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                {
                    var step = e.Delta > 0 ? -10 : 10;
                    SetKlineCount(KlineCount + step);
                }
                else
                    CycleKlinePeriod(e.Delta);
            }
            return;
        }
        if (Stocks.Count < 2) return;
        if (e.Delta > 0) SwitchTo((Index - 1 + Stocks.Count) % Stocks.Count);
        else if (e.Delta < 0) SwitchTo((Index + 1) % Stocks.Count);
    }

    bool OverChart(Point pos)
    {
        if (Chart.Visibility != Visibility.Visible || Chart.ActualWidth < 8 || Chart.ActualHeight < 8)
            return false;
        var origin = Chart.TranslatePoint(new Point(0, 0), this);
        return new Rect(origin, new Size(Chart.ActualWidth, Chart.ActualHeight)).Contains(pos);
    }

    void OnContext(object sender, MouseButtonEventArgs e)
    {
        MarkActivity();
        var menu = BuildMenu();
        menu.PlacementTarget = this;
        menu.IsOpen = true;
        e.Handled = true;
    }

    void BuildTray()
    {
        Tray = new WinForms.NotifyIcon
        {
            Icon = TrayIconFactory.Make("gray"),
            Visible = true,
            Text = "FloatQuote · 悬浮行情",
        };
        Tray.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right)
            {
                var menu = BuildMenu();
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.IsOpen = true;
            }
            else if (e.Button == WinForms.MouseButtons.Left)
                ToggleVisible();
        };
        Tray.DoubleClick += (_, _) => ToggleVisible();
    }

    void SavePos()
    {
        Cfg.Pos = [(int)Left, (int)Top];
        ConfigStore.Save(Cfg);
    }

    void SaveSize()
    {
        if (Expanded)
        {
            _expandedSize = [Width, Height];
            Cfg.SizeExpanded = [(int)Width, (int)Height];
        }
        else
        {
            _collapsedSize = [Width, Height];
            Cfg.SizeCollapsed = [(int)Width, (int)Height];
        }
        ConfigStore.Save(Cfg);
    }

    static double[] LoadSize(int[]? v, double[] fallback, (double W, double H) min)
    {
        if (v is { Length: 2 })
            return [Math.Max(v[0], min.W), Math.Max(v[1], min.H)];
        return fallback;
    }

    static bool IsLegacyExpandedSize(int[]? v)
        => v is not { Length: 2 }
           || (v[0] <= 200 && v[1] <= 170)
           || (v[0] <= 330 && v[1] <= 290);

    void ClampToWorkArea()
    {
        var wa = SystemParameters.WorkArea;
        if (Left + Width > wa.Right)
            Left = Math.Max(wa.Left, wa.Right - Width);
        if (Top + Height > wa.Bottom)
            Top = Math.Max(wa.Top, wa.Bottom - Height);
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
    }

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T match) return match;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    static MenuItem Item(string header, Action action)
    {
        var it = new MenuItem { Header = header };
        it.Click += (_, _) => action();
        return it;
    }

    static Cursor CursorFor(string? edge) => edge switch
    {
        "l" or "r" => Cursors.SizeWE,
        "t" or "b" => Cursors.SizeNS,
        "tl" or "br" => Cursors.SizeNWSE,
        "tr" or "bl" => Cursors.SizeNESW,
        _ => Cursors.Arrow,
    };

    void ApplyToolWindow()
    {
        var helper = new WindowInteropHelper(this);
        var ex = GetWindowLong(helper.Handle, GwlExstyle);
        SetWindowLong(helper.Handle, GwlExstyle, ex | WsExToolwindow);
    }

    const int GwlExstyle = -20;
    const int WsExToolwindow = 0x00000080;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}

using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FloatQuote;

public sealed class StockEditDialog : Window
{
    public static bool SuppressMessages { get; set; }

    readonly List<WatchCategory> _cats;
    readonly Dictionary<string, string> _names;
    readonly List<ListEntry> _entries = [];
    bool _searching;

    public TextBox SearchEdit { get; }
    public ListBox ListWidget { get; }
    readonly Button _upBtn, _downBtn, _delBtn, _newCatBtn, _toggleBtn;

    public StockEditDialog(IEnumerable<string> codes, IDictionary<string, string> names,
        IEnumerable<WatchCategory>? categories = null)
    {
        Title = "自选股管理";
        Width = 460;
        Height = 480;
        MinWidth = 420;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Theme.DialogBg);
        Foreground = new SolidColorBrush(Theme.TextMain);
        FontFamily = new FontFamily(Theme.UiFont);
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ResizeMode = ResizeMode.CanResize;

        _names = new Dictionary<string, string>(names, StringComparer.OrdinalIgnoreCase);
        _cats = categories is not null
            ? ConfigStore.CloneCategories(categories)
            : [];
        if (_cats.All(c => c.Stocks.Count == 0))
        {
            var tmp = new AppConfig { Stocks = codes.ToList(), Categories = _cats };
            _cats.Clear();
            _cats.AddRange(ConfigStore.CloneCategories(ConfigStore.EnsureCategories(tmp)));
        }
        if (_cats.Count == 0)
            _cats.Add(new WatchCategory { Name = ConfigStore.CatAShare, Visible = true });

        SearchEdit = FieldBox();
        SetPlaceholder(SearchEdit, "输入代码或名称，如 茅台 / 伦敦金 / sh600519");
        var addBtn = DarkButton("添加");
        addBtn.ToolTip = "按代码或名称搜索添加";

        var searchRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(addBtn, Dock.Right);
        addBtn.Margin = new Thickness(8, 0, 0, 0);
        searchRow.Children.Add(addBtn);
        searchRow.Children.Add(SearchEdit);

        ListWidget = new ListBox
        {
            Background = new SolidColorBrush(Theme.FieldBg),
            Foreground = new SolidColorBrush(Theme.TextMain),
            BorderBrush = new SolidColorBrush(Theme.Border),
            BorderThickness = new Thickness(1),
        };
        ListWidget.SelectionChanged += (_, _) => UpdateButtons();

        _upBtn = DarkButton("上移");
        _downBtn = DarkButton("下移");
        _delBtn = DarkButton("删除");
        _newCatBtn = DarkButton("新建分类");
        _toggleBtn = DarkButton("显示/隐藏");
        foreach (var b in new[] { _upBtn, _downBtn, _delBtn, _toggleBtn }) b.IsEnabled = false;
        var btnRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var b in new[] { _upBtn, _downBtn, _delBtn, _newCatBtn, _toggleBtn })
        {
            b.Margin = new Thickness(0, 0, 8, 4);
            btnRow.Children.Add(b);
        }

        var hint = new TextBlock
        {
            Text = "勾选分类可显示/隐藏。A 股休市时可关掉「A股」，只看黄金。",
            Foreground = new SolidColorBrush(Theme.TextSub),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var okBtn = DarkButton("确定");
        okBtn.Background = new SolidColorBrush(Color.FromRgb(62, 107, 79));
        var cancelBtn = DarkButton("取消");
        var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        okBtn.Margin = new Thickness(0, 0, 8, 0);
        bottom.Children.Add(okBtn);
        bottom.Children.Add(cancelBtn);

        var root = new DockPanel { Margin = new Thickness(14) };
        var south = new StackPanel();
        south.Children.Add(btnRow);
        south.Children.Add(hint);
        south.Children.Add(bottom);
        DockPanel.SetDock(south, Dock.Bottom);
        DockPanel.SetDock(searchRow, Dock.Top);
        root.Children.Add(south);
        root.Children.Add(searchRow);
        root.Children.Add(ListWidget);
        Content = root;

        addBtn.Click += (_, _) => AddFromSearch();
        SearchEdit.KeyDown += (_, e) => { if (e.Key == Key.Enter) AddFromSearch(); };
        _upBtn.Click += (_, _) => MoveUp();
        _downBtn.Click += (_, _) => MoveDown();
        _delBtn.Click += (_, _) => DeleteSelected();
        _newCatBtn.Click += (_, _) => AddCategory();
        _toggleBtn.Click += (_, _) => ToggleSelectedCategory();
        okBtn.Click += (_, _) => { DialogResult = true; Close(); };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        ListWidget.MouseDoubleClick += (_, _) =>
        {
            if (CurrentEntry() is { IsCategory: false } && ListWidget.SelectedIndex >= 0)
            {
                DialogResult = true;
                Close();
            }
        };

        FetchMissingNames();
        RefreshList();
        DarkTitleBar.Apply(this);
    }

    public List<string> Codes() => _cats.SelectMany(c => c.Stocks).ToList();

    public List<WatchCategory> Categories() => ConfigStore.CloneCategories(_cats);

    public string? NameOf(string code) => _names.TryGetValue(code, out var n) ? n : null;

    public List<string> ListLabels()
        => [.. ListWidget.Items.OfType<ListBoxItem>().Select(i => i.Tag as string ?? i.Content?.ToString() ?? "")];

    public void SelectNthStock(int n)
    {
        var idx = _entries.Select((e, i) => (e, i)).Where(x => !x.e.IsCategory).Skip(n).Select(x => x.i).FirstOrDefault(-1);
        if (idx >= 0) ListWidget.SelectedIndex = idx;
    }

    public void AddFromSearch()
    {
        var text = SearchEdit.Text.Trim();
        if (text.Length == 0 || text.Contains("输入代码或名称") || _searching)
            return;
        _searching = true;
        Mouse.OverrideCursor = Cursors.Wait;
        List<(string Code, string Name)> results;
        try
        {
            try { results = QuoteClient.SearchStocks(text); }
            catch { results = []; }
            if (results.Count == 0 && ConfigStore.CodeRe.IsMatch(text.ToLowerInvariant()))
            {
                try
                {
                    var canonical = ConfigStore.CanonicalCode(text);
                    var qs = QuoteClient.FetchQuotes([canonical]);
                    if (qs.TryGetValue(canonical, out var q) && q.Name is not ("--" or ""))
                        results = [(canonical, q.Name)];
                }
                catch { }
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
            _searching = false;
        }

        if (results.Count == 0)
        {
            Info(this, "未找到", $"未找到与「{text}」匹配的品种。\n可搜股票名称/代码，或「伦敦金」「COMEX」。");
            return;
        }

        string code, name;
        if (results.Count > 1)
        {
            if (SuppressMessages)
                (code, name) = results[0];
            else
            {
                var labels = results.Select(r => $"{r.Name}  {r.Code}").ToList();
                var pick = ChoiceDialog.Pick(this, "选择股票", "找到多个匹配，请选择：", labels);
                if (pick is null) return;
                (code, name) = results[labels.IndexOf(pick)];
            }
        }
        else (code, name) = results[0];

        if (Codes().Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            Info(this, "已在列表", $"{name}（{code}）已在自选股中。");
            return;
        }
        _names[code] = name;
        AddCodeToCategory(code);
        RefreshList();
        SearchEdit.Clear();
        SelectCode(code);
    }

    public void MoveUp()
    {
        if (CurrentEntry() is not { } e) return;
        if (e.IsCategory)
        {
            if (e.Cat <= 0) return;
            (_cats[e.Cat - 1], _cats[e.Cat]) = (_cats[e.Cat], _cats[e.Cat - 1]);
            RefreshList();
            SelectCategory(e.Cat - 1);
            return;
        }
        var cat = _cats[e.Cat];
        if (e.Stock > 0)
        {
            (cat.Stocks[e.Stock - 1], cat.Stocks[e.Stock]) = (cat.Stocks[e.Stock], cat.Stocks[e.Stock - 1]);
            RefreshList();
            SelectNthInCategory(e.Cat, e.Stock - 1);
            return;
        }
        if (e.Cat == 0) return;
        var code = cat.Stocks[e.Stock];
        cat.Stocks.RemoveAt(e.Stock);
        _cats[e.Cat - 1].Stocks.Add(code);
        RefreshList();
        SelectCode(code);
    }

    public void MoveDown()
    {
        if (CurrentEntry() is not { } e) return;
        if (e.IsCategory)
        {
            if (e.Cat >= _cats.Count - 1) return;
            (_cats[e.Cat], _cats[e.Cat + 1]) = (_cats[e.Cat + 1], _cats[e.Cat]);
            RefreshList();
            SelectCategory(e.Cat + 1);
            return;
        }
        var cat = _cats[e.Cat];
        if (e.Stock < cat.Stocks.Count - 1)
        {
            (cat.Stocks[e.Stock], cat.Stocks[e.Stock + 1]) = (cat.Stocks[e.Stock + 1], cat.Stocks[e.Stock]);
            RefreshList();
            SelectNthInCategory(e.Cat, e.Stock + 1);
            return;
        }
        if (e.Cat >= _cats.Count - 1) return;
        var code = cat.Stocks[e.Stock];
        cat.Stocks.RemoveAt(e.Stock);
        _cats[e.Cat + 1].Stocks.Insert(0, code);
        RefreshList();
        SelectCode(code);
    }

    public void DeleteSelected()
    {
        if (CurrentEntry() is not { } e) return;
        if (e.IsCategory)
        {
            if (_cats.Count <= 1)
            {
                Info(this, "无法删除", "至少保留一个分类。");
                return;
            }
            var moving = _cats[e.Cat].Stocks;
            var dest = e.Cat > 0 ? _cats[e.Cat - 1] : _cats[e.Cat + 1];
            dest.Stocks.AddRange(moving);
            _cats.RemoveAt(e.Cat);
            RefreshList();
            return;
        }
        _cats[e.Cat].Stocks.RemoveAt(e.Stock);
        if (_cats[e.Cat].Stocks.Count == 0 && _cats.Count > 1)
            _cats.RemoveAt(e.Cat);
        RefreshList();
    }

    public void AddCategory(string? name = null)
    {
        name ??= TextPrompt.Ask(this, "新建分类", "分类名称：", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (_cats.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Info(this, "已存在", $"分类「{name}」已存在。");
            return;
        }
        _cats.Add(new WatchCategory { Name = name, Visible = true });
        RefreshList();
        SelectCategory(_cats.Count - 1);
    }

    public void ToggleSelectedCategory()
    {
        if (CurrentEntry() is not { } e) return;
        var cat = _cats[e.Cat];
        if (cat.Visible && !CanHide(cat))
        {
            Info(this, "无法隐藏", "至少保留一个已显示且有品种的分类。");
            return;
        }
        cat.Visible = !cat.Visible;
        RefreshList();
        SelectCategory(e.Cat);
    }

    void AddCodeToCategory(string code)
    {
        WatchCategory? cat = null;
        if (CurrentEntry() is { } e && e.Cat >= 0 && e.Cat < _cats.Count)
            cat = _cats[e.Cat];
        var want = ConfigStore.DefaultCategory(code);
        cat ??= _cats.FirstOrDefault(c => c.Name.Equals(want, StringComparison.OrdinalIgnoreCase));
        if (cat is null)
        {
            cat = new WatchCategory { Name = want, Visible = true };
            _cats.Add(cat);
        }
        cat.Stocks.Add(code);
    }

    bool CanHide(WatchCategory cat)
        => _cats.Any(c => !ReferenceEquals(c, cat) && c.Visible && c.Stocks.Count > 0);

    void FetchMissingNames()
    {
        var missing = Codes().Where(c => !_names.ContainsKey(c) || string.IsNullOrEmpty(_names[c])).ToList();
        if (missing.Count == 0) return;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var qs = QuoteClient.FetchQuotes(missing);
            foreach (var (c, q) in qs)
            {
                if (q.Name is not ("--" or ""))
                    _names[c] = q.Name;
            }
        }
        catch { }
        finally { Mouse.OverrideCursor = null; }
    }

    void RefreshList()
    {
        var sel = CurrentEntry();
        ListWidget.Items.Clear();
        _entries.Clear();
        for (var ci = 0; ci < _cats.Count; ci++)
        {
            var cat = _cats[ci];
            var catIdx = ci;
            _entries.Add(new ListEntry(true, ci, -1));
            var mark = cat.Visible ? "☑" : "☐";
            var head = $"{mark}  {cat.Name}    {(cat.Visible ? "显示" : "隐藏")}  ({cat.Stocks.Count})";
            ListWidget.Items.Add(MakeItem(head, true));

            for (var si = 0; si < cat.Stocks.Count; si++)
            {
                var code = cat.Stocks[si];
                _names.TryGetValue(code, out var name);
                var label = string.IsNullOrEmpty(name) ? $"      {code}" : $"      {name}  {code}";
                _entries.Add(new ListEntry(false, catIdx, si));
                ListWidget.Items.Add(MakeItem(label, false));
            }
        }
        UpdateButtons();
        if (sel is { } keep)
        {
            var idx = _entries.FindIndex(e => e.IsCategory == keep.IsCategory && e.Cat == keep.Cat
                && (keep.IsCategory || e.Stock == keep.Stock));
            if (idx >= 0) ListWidget.SelectedIndex = idx;
        }
    }

    static ListBoxItem MakeItem(string text, bool category)
        => new()
        {
            Content = text,
            Tag = text,
            FontWeight = category ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(category ? Theme.Yellow : Theme.TextMain),
            Padding = new Thickness(6, 4, 6, 4),
        };

    ListEntry? CurrentEntry()
    {
        var row = ListWidget.SelectedIndex;
        if (row < 0 || row >= _entries.Count) return null;
        return _entries[row];
    }

    void SelectCode(string code)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            if (!e.IsCategory && _cats[e.Cat].Stocks[e.Stock].Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                ListWidget.SelectedIndex = i;
                ListWidget.ScrollIntoView(ListWidget.Items[i]);
                return;
            }
        }
    }

    void SelectCategory(int cat)
    {
        var idx = _entries.FindIndex(e => e.IsCategory && e.Cat == cat);
        if (idx >= 0) ListWidget.SelectedIndex = idx;
    }

    void SelectNthInCategory(int cat, int stock)
    {
        var idx = _entries.FindIndex(e => !e.IsCategory && e.Cat == cat && e.Stock == stock);
        if (idx >= 0) ListWidget.SelectedIndex = idx;
    }

    void UpdateButtons()
    {
        var e = CurrentEntry();
        var has = e is not null;
        _delBtn.IsEnabled = has;
        _toggleBtn.IsEnabled = has;
        if (e is not { } cur)
        {
            _upBtn.IsEnabled = false;
            _downBtn.IsEnabled = false;
            return;
        }
        if (cur.IsCategory)
        {
            _upBtn.IsEnabled = cur.Cat > 0;
            _downBtn.IsEnabled = cur.Cat < _cats.Count - 1;
        }
        else
        {
            var last = cur.Cat == _cats.Count - 1 && cur.Stock == _cats[cur.Cat].Stocks.Count - 1;
            _upBtn.IsEnabled = cur.Cat > 0 || cur.Stock > 0;
            _downBtn.IsEnabled = !last;
        }
    }

    static void Info(Window owner, string title, string text)
    {
        if (SuppressMessages) return;
        MessageBox.Show(owner, text, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    static TextBox FieldBox() => new()
    {
        Background = new SolidColorBrush(Theme.FieldBg),
        Foreground = new SolidColorBrush(Theme.TextMain),
        BorderBrush = new SolidColorBrush(Theme.Border),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(6, 4, 6, 4),
        CaretBrush = new SolidColorBrush(Theme.TextMain),
    };

    static Button DarkButton(string text) => new()
    {
        Content = text,
        Background = new SolidColorBrush(Color.FromRgb(44, 49, 64)),
        Foreground = new SolidColorBrush(Theme.TextMain),
        BorderBrush = new SolidColorBrush(Color.FromRgb(58, 65, 80)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(14, 4, 14, 4),
        MinWidth = 64,
    };

    static void SetPlaceholder(TextBox box, string placeholder)
    {
        box.Tag = "empty";
        box.Text = placeholder;
        box.Foreground = new SolidColorBrush(Theme.TextSub);
        box.GotFocus += (_, _) =>
        {
            if (Equals(box.Tag, "empty"))
            {
                box.Text = "";
                box.Tag = "edit";
                box.Foreground = new SolidColorBrush(Theme.TextMain);
            }
        };
        box.LostFocus += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                box.Text = placeholder;
                box.Tag = "empty";
                box.Foreground = new SolidColorBrush(Theme.TextSub);
            }
        };
    }

    readonly record struct ListEntry(bool IsCategory, int Cat, int Stock);
}

public sealed class ChoiceDialog : Window
{
    public static string? Pick(Window owner, string title, string prompt, IReadOnlyList<string> items)
    {
        var dlg = new ChoiceDialog(title, prompt, items) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._chosen : null;
    }

    string? _chosen;

    ChoiceDialog(string title, string prompt, IReadOnlyList<string> items)
    {
        Title = title;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Theme.DialogBg);
        Foreground = new SolidColorBrush(Theme.TextMain);
        FontFamily = new FontFamily(Theme.UiFont);
        UseLayoutRounding = true;
        ResizeMode = ResizeMode.NoResize;

        var list = new ListBox
        {
            ItemsSource = items,
            SelectedIndex = 0,
            MaxHeight = 240,
            Background = new SolidColorBrush(Theme.FieldBg),
            Foreground = new SolidColorBrush(Theme.TextMain),
            BorderBrush = new SolidColorBrush(Theme.Border),
        };
        var ok = new Button { Content = "确定", IsDefault = true, Width = 80, Margin = new Thickness(0, 8, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 80, Margin = new Thickness(0, 8, 0, 0) };
        ok.Click += (_, _) =>
        {
            _chosen = list.SelectedItem as string;
            DialogResult = _chosen is not null;
            Close();
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        root.Children.Add(list);
        root.Children.Add(btns);
        Content = root;
        DarkTitleBar.Apply(this);
    }
}

public sealed class TextPrompt : Window
{
    public static string? Ask(Window? owner, string title, string prompt, string initial, bool digitsOnly = false)
    {
        if (StockEditDialog.SuppressMessages)
            return string.IsNullOrEmpty(initial) ? null : initial;
        var dlg = new TextPrompt(title, prompt, initial, digitsOnly);
        if (owner is not null) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg._value : null;
    }

    public static int? AskInt(Window? owner, string title, string prompt, int current, int min = 1, int max = 99)
    {
        while (true)
        {
            var text = Ask(owner, title, prompt, current.ToString(CultureInfo.InvariantCulture), digitsOnly: true);
            if (text is null) return null;
            if (int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= min)
                return Math.Min(n, max);
            if (StockEditDialog.SuppressMessages) return null;
            MessageBox.Show(owner, $"请输入 {min} 到 {max} 的正整数。", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    string? _value;

    TextPrompt(string title, string prompt, string initial, bool digitsOnly)
    {
        Title = title;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = Owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Theme.DialogBg);
        Foreground = new SolidColorBrush(Theme.TextMain);
        FontFamily = new FontFamily(Theme.UiFont);
        UseLayoutRounding = true;
        ResizeMode = ResizeMode.NoResize;

        var box = new TextBox
        {
            Text = initial,
            Background = new SolidColorBrush(Theme.FieldBg),
            Foreground = new SolidColorBrush(Theme.TextMain),
            BorderBrush = new SolidColorBrush(Theme.Border),
            CaretBrush = new SolidColorBrush(Theme.TextMain),
            Padding = new Thickness(6, 4, 6, 4),
        };
        if (digitsOnly)
        {
            box.PreviewTextInput += (_, e) => e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
            DataObject.AddPastingHandler(box, (_, e) =>
            {
                if (e.DataObject.GetDataPresent(DataFormats.Text)
                    && !Regex.IsMatch((string)e.DataObject.GetData(DataFormats.Text)!, @"^\d+$"))
                    e.CancelCommand();
            });
        }
        var ok = new Button { Content = "确定", IsDefault = true, Width = 80, Margin = new Thickness(0, 10, 8, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Width = 80, Margin = new Thickness(0, 10, 0, 0) };
        ok.Click += (_, _) =>
        {
            _value = box.Text.Trim();
            DialogResult = true;
            Close();
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(box);
        root.Children.Add(btns);
        Content = root;
        Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        DarkTitleBar.Apply(this);
    }
}

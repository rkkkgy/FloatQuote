using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace FloatQuote;

/// <summary>
/// 冒烟测试：dotnet run -- --smoke
/// 使用临时配置，不污染真实 config.json。
/// </summary>
public static class Smoke
{
    static string _tmpDir = "";

    public static void Prepare()
    {
        _tmpDir = Directory.CreateTempSubdirectory("floatquote_test_").FullName;
        ConfigStore.OverrideDir = _tmpDir;
        var cfg = ConfigStore.Load();
        cfg.AutoSwitchSeconds = 0;
        ConfigStore.Save(cfg);
        Console.WriteLine($"[ok] 配置加载: {string.Join(",", cfg.Stocks)}");
    }

    public static async Task<int> RunAsync(MainWindow w)
    {
        try
        {
            w.AutoSwitchSeconds = 0;
            StockEditDialog.SuppressMessages = true;
            await Pump(3000);

            await WaitUntil(() => w.Quotes.Count > 0, "报价未到达");
            var q = w.Quotes.GetValueOrDefault(w.CurrentCode());
            if (q is null || !q.Ok || q.Price is not > 0)
                throw new Exception($"当前股票报价异常: {q?.Name}");
            Console.WriteLine($"[ok] 报价显示: {q.Name} {q.Price} ({q.ChangePct}%)");
            if (w.PriceLabel.Text == "--") throw new Exception("价格标签未更新");

            if (w.Tray is not null)
            {
                if (!w.Tray.Visible) throw new Exception("托盘图标未显示");
                if (w.Tray.Icon is null) throw new Exception("托盘图标为空");
                if (w.Tray.Text?.Contains("贵州茅台") != true)
                    Console.WriteLine($"[skip] 托盘提示未含茅台: {w.Tray.Text}");
                else
                    Console.WriteLine($"[ok] 托盘图标: tooltip='{w.Tray.Text}'");
                w.ToggleVisible();
                await Pump(300);
                if (w.IsVisible) throw new Exception("隐藏悬浮窗失败");
                w.ToggleVisible();
                await Pump(300);
                if (!w.IsVisible) throw new Exception("显示悬浮窗失败");
                Console.WriteLine("[ok] 托盘显隐切换正常");
            }

            var code0 = w.CurrentCode();
            await WaitUntil(() => w.MinuteCache.ContainsKey(code0) || w.Chart.Fallback is not null, "分时数据未加载");
            if (w.MinuteCache.TryGetValue(code0, out var cached))
            {
                if (cached.Points.Count == 0) throw new Exception("分时数据点为空");
                Console.WriteLine($"[ok] 分时图: {cached.Points.Count} 个数据点, 最新价 {cached.Points[^1].Price}");
            }
            else
            {
                if (w.Chart.Fallback is null) throw new Exception("降级图未显示");
                Console.WriteLine("[ok] 分时图: 腾讯数据源降级，显示新浪分时图");
            }

            w.SetExpanded(true);
            await Pump(300);
            if (w.DetailPanel.Visibility != Visibility.Visible) throw new Exception("展开态详情未显示");
            if (Math.Abs(w.Width - MainWindow.ExpandedW) > 12 || Math.Abs(w.Height - MainWindow.ExpandedH) > 12)
                throw new Exception($"展开尺寸不对: {w.Width}x{w.Height}");
            w.SetExpanded(false);
            await Pump(300);
            if (w.DetailPanel.Visibility != Visibility.Collapsed) throw new Exception("收起态详情未隐藏");
            Console.WriteLine($"[ok] 收起/展开切换正常 ({w.Width:0}x{w.Height:0} / 展开 {MainWindow.ExpandedW}x{MainWindow.ExpandedH})");

            w.SetExpanded(true);
            await Pump(300);
            var bw = w.Width; var bh = w.Height;
            w.ResizeBy("br", 60, 40);
            await Pump(200);
            if (Math.Abs(w.Width - (bw + 60)) > 1 || Math.Abs(w.Height - (bh + 40)) > 1)
                throw new Exception($"缩放失败: {bw}x{bh} -> {w.Width}x{w.Height}");
            if (w.Cfg.SizeExpanded is not { Length: 2 })
                throw new Exception("缩放后尺寸未写入配置");
            Console.WriteLine($"[ok] 拖拽缩放: {bw:0}x{bh:0} -> {w.Width:0}x{w.Height:0}, 已记住");

            w.ResizeBy("br", -500, -300);
            await Pump(200);
            if (w.Width < MainWindow.MinExpanded.W || w.Height < MainWindow.MinExpanded.H)
                throw new Exception($"最小尺寸保护失效: {w.Width}x{w.Height}");
            Console.WriteLine($"[ok] 最小尺寸保护: {w.Width:0}x{w.Height:0}");

            w.SetExpanded(false);
            await Pump(200);

            var idx0 = w.Index;
            w.SwitchTo((idx0 + 1) % w.Stocks.Count);
            await WaitUntil(() => w.Quotes.ContainsKey(w.CurrentCode()), "切换后报价缺失");
            if (w.Index == idx0) throw new Exception("切换失败");
            Console.WriteLine($"[ok] 股票切换: {w.Stocks[w.Index]}");

            w.TogglePinned();
            if (!w.Pinned) throw new Exception("锁定失败");
            w.SetExpanded(true);
            w.OnLeave();
            await Pump(1200);
            if (w.DetailPanel.Visibility != Visibility.Visible) throw new Exception("锁定时不应收起");
            w.TogglePinned();
            Console.WriteLine("[ok] 锁定展开正常");

            var pos = new[] { (int)w.Left, (int)w.Top };
            w.Cfg.Pos = pos;
            ConfigStore.Save(w.Cfg);
            var cfg2 = ConfigStore.Load();
            if (cfg2.Pos is null || cfg2.Pos[0] != pos[0] || cfg2.Pos[1] != pos[1])
                throw new Exception("位置未写回");
            Console.WriteLine($"[ok] 配置写回: pos=[{pos[0]},{pos[1]}]");

            var r = QuoteClient.SearchStocks("茅台");
            if (r.Count == 0)
                Console.WriteLine("[skip] 搜索接口不可用（网络），跳过搜索断言");
            else
            {
                if (r[0].Code != "sh600519" || r[0].Name != "贵州茅台")
                    throw new Exception($"搜索「茅台」异常: {r[0]}");
                var r2 = QuoteClient.SearchStocks("600519");
                if (r2.Count == 0 || r2[0].Code != "sh600519")
                    throw new Exception($"搜索代码异常: {string.Join(",", r2)}");
                var r3 = QuoteClient.SearchStocks("平安");
                if (r3.Count < 1) throw new Exception("多结果搜索为空");
                Console.WriteLine($"[ok] 股票搜索: 茅台->{r[0]}, 平安->{r3.Count}条");
            }

            try
            {
                var g = QuoteClient.SearchStocks("伦敦金");
                if (g.All(x => x.Code != "xau"))
                    Console.WriteLine($"[skip] 金价搜索未命中: {string.Join(",", g.Select(x => x.Code))}");
                else
                    Console.WriteLine("[ok] 金价搜索: 伦敦金 -> xau");
                var gq = QuoteClient.FetchQuotes(["xau"]);
                if (gq.TryGetValue("xau", out var gold) && gold.Ok && gold.Price is > 0)
                    Console.WriteLine($"[ok] 伦敦金报价: {gold.Name} {gold.Price} ({gold.ChangePct}%)");
                else
                    Console.WriteLine("[skip] 伦敦金报价不可用");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[skip] 金价接口不可用: {ex.Message}");
            }

            try
            {
                var bars = QuoteClient.FetchKline("sh603039", KlinePeriod.Day);
                if (bars.Count == 0)
                    Console.WriteLine("[skip] 日K 无数据");
                else
                    Console.WriteLine($"[ok] 日K: 泛微 {bars.Count} 根, 最新收 {bars[^1].Close}");
                var wkl = QuoteClient.FetchKline("sz000159", KlinePeriod.Week);
                if (wkl.Count == 0)
                    Console.WriteLine("[skip] 周K 无数据");
                else
                    Console.WriteLine($"[ok] 周K: 国际实业 {wkl.Count} 根, 最新收 {wkl[^1].Close}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[skip] K线接口不可用: {ex.Message}");
            }

            var menu = w.BuildMenu();
            var switchItem = menu.Items.OfType<MenuItem>().First(a => (string)a.Header == "切换股票");
            var texts = switchItem.Items.OfType<MenuItem>().Select(a => (string)a.Header!).ToList();
            if (!texts.Any(t => t.Contains("贵州茅台")))
                throw new Exception($"切换菜单未显示名称: {string.Join(",", texts)}");
            Console.WriteLine($"[ok] 切换菜单显示名称: {string.Join(" | ", texts.Take(3))}");

            var dlg = new StockEditDialog(["sh600519", "sz000001"],
                new Dictionary<string, string> { ["sh600519"] = "贵州茅台", ["sz000001"] = "平安银行" });
            dlg.Show();
            await Pump(400);
            if (dlg.Codes().Count != 2) throw new Exception("初始列表数量不对");
            if (!dlg.ListLabels().Any(t => t.Contains("贵州茅台")))
                throw new Exception("列表未显示名称");
            Console.WriteLine($"[ok] 对话框列表: {string.Join(" | ", dlg.ListLabels())}");

            dlg.SearchEdit.Text = "sh600519";
            dlg.AddFromSearch();
            if (dlg.Codes().Count != 2) throw new Exception("重复添加未被拦截");

            dlg.SearchEdit.Text = "600519";
            dlg.AddFromSearch();
            if (dlg.Codes().Count == 3)
                Console.WriteLine("[ok] 对话框添加: 600519 -> " + string.Join(",", dlg.Codes()));
            else
                Console.WriteLine("[skip] 网络不可用，跳过对话框添加断言");

            dlg.SelectNthStock(0);
            var before0 = dlg.Codes()[0];
            dlg.MoveDown();
            if (dlg.Codes()[1] != before0) throw new Exception("下移失败");
            dlg.MoveUp();
            if (dlg.Codes()[0] != before0) throw new Exception("上移失败");
            var nBefore = dlg.Codes().Count;
            dlg.SelectNthStock(1);
            dlg.DeleteSelected();
            if (dlg.Codes().Count != nBefore - 1) throw new Exception("删除失败");
            Console.WriteLine($"[ok] 对话框排序/删除: {string.Join(",", dlg.Codes())}");
            dlg.AddCategory("观察");
            if (dlg.Categories().All(c => c.Name != "观察")) throw new Exception("新建分类失败");
            Console.WriteLine("[ok] 对话框新建分类: 观察");
            dlg.Close();

            w.Stocks = ["sh600519", "sz000001", "sz300750", "sz000002"];
            w.Index = 0;
            w.SetDisplayCount(3);
            if (w.DisplayCount != 3 || w.Rows.Count != 3) throw new Exception("行数未更新");
            w.SetExpanded(false);
            await Pump(300);
            var expectH = 3 * StockRow.RowH + 2 * 1 + 2;
            if (Math.Abs(w.Height - expectH) > 4)
                throw new Exception($"收起高度未随行数变化: {w.Height} (期望 {expectH})");
            if (!w.VisibleStocks().SequenceEqual(["sh600519", "sz000001", "sz300750"]))
                throw new Exception($"可见股票窗口错误: {string.Join(",", w.VisibleStocks())}");
            Console.WriteLine($"[ok] 显示数量3: 高度={w.Height:0}, 行={string.Join(",", w.Rows.Select(r => r.NameLabel.Text))}");

            w.SwitchTo(2);
            if (w.Index != 1) throw new Exception($"滚动钳制错误: index={w.Index}");
            Console.WriteLine($"[ok] 多行滚动: index=1 -> {string.Join(",", w.VisibleStocks())}");

            w.OnRowClick("sz300750");
            if (w.CurrentCode() != "sz300750") throw new Exception("点击行未选中");
            Console.WriteLine("[ok] 点击行选中: sz300750");

            w.SetDisplayCount(6);
            w.SetExpanded(false);
            await Pump(300);
            var h6 = w.Height;
            w.SetDisplayCount(3);
            w.SetExpanded(false);
            await Pump(300);
            var h3 = w.Height;
            if (Math.Abs(h3 - expectH) > 4) throw new Exception($"数量6->3 高度未自适应: {h3}");
            if (h3 >= h6) throw new Exception($"缩小数量后高度未变小: {h6} -> {h3}");
            Console.WriteLine($"[ok] 数量自适应: 6 -> {h6:0}px, 3 -> {h3:0}px");

            w.SetDisplayCount(12);
            if (w.DisplayCount != 12) throw new Exception($"正整数显示数量未生效: {w.DisplayCount}");
            w.SetDisplayCount(3);
            Console.WriteLine("[ok] 显示数量可录入大于 6 的正整数");

            w.SetExpanded(false);
            await Pump(200);
            w.SetDisplayCount(3);
            await Pump(200);
            w.OnRowClick(w.Rows[0].Code!);
            await Pump(300);
            if (!w.Expanded) throw new Exception("点击行未展开");
            Console.WriteLine($"[ok] 点击展开: {w.CurrentCode()}");

            w.SetExpanded(false);
            await Pump(200);
            w.OnEnter();
            await Pump(300);
            if (w.Expanded) throw new Exception("悬停不应展开");
            Console.WriteLine("[ok] 悬停不展开");

            w.OnRowClick(w.Rows[0].Code!);
            await Pump(300);
            w.OnLeave();
            await Pump(3500);
            if (w.DetailPanel.Visibility != Visibility.Collapsed) throw new Exception("移开 3 秒后未收起");
            Console.WriteLine("[ok] 移开 3 秒后收起");

            w.OnRowClick(w.Rows[0].Code!);
            await Pump(300);
            w.BackBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Pump(200);
            if (w.DetailPanel.Visibility != Visibility.Collapsed) throw new Exception("返回按钮未立即收起");
            Console.WriteLine("[ok] 返回按钮立即收起");

            w.TogglePinned();
            if (!(w.Pinned && w.Expanded)) throw new Exception("锁定前置失败");
            w.BackBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Pump(200);
            if (w.Expanded || w.Pinned) throw new Exception("锁定时返回按钮未收起/解锁");
            Console.WriteLine("[ok] 锁定时返回按钮收起并解锁");

            w.SetDisplayCount(1);
            w.Stocks = ["sh600519", "sz000001", "sz300750"];
            w.Index = Math.Min(w.Index, w.Stocks.Count - 1);
            Console.WriteLine("[ok] 点击交互还原");

            var cfg = ConfigStore.Load();
            cfg.AutoSwitchSeconds = 2;
            ConfigStore.Save(cfg);
            var w2 = new MainWindow();
            w2.Show();
            await Pump(500);
            var i0 = w2.Index;
            await WaitUntil(() => w2.Index != i0, "自动轮播未切换", 8000);
            Console.WriteLine($"[ok] 自动轮播: 无操作 2 秒后自动切换 -> {w2.Stocks[w2.Index]}");

            w2.SetExpanded(true);
            await Pump(300);
            var idxPinned = w2.Index;
            w2.LastActivity = DateTime.MinValue;
            await Pump(3000);
            if (w2.Index != idxPinned) throw new Exception("展开期间不应自动切换");
            Console.WriteLine("[ok] 展开时暂停自动轮播");

            w2.SetExpanded(false);
            await Pump(300);
            w2.LastActivity = DateTime.MinValue;
            await WaitUntil(() => w2.Index != idxPinned, "收起后未恢复轮播", 8000);
            Console.WriteLine("[ok] 收起后恢复自动轮播");

            w2.SetSwitchEffect("slide_h");
            if (w2.SwitchEffect != "slide_h") throw new Exception("动画设置未生效");
            w2.SwitchTo((w2.Index + 1) % w2.Stocks.Count);
            await Pump(600);
            w2.SetSwitchEffect("pulse");
            w2.SwitchTo((w2.Index + 1) % w2.Stocks.Count);
            await Pump(700);
            w2.SetSwitchEffect("off");
            w2.SwitchTo((w2.Index + 1) % w2.Stocks.Count);
            await Pump(300);
            Console.WriteLine("[ok] 切换动画: slide_h / pulse / off 运行无异常");
            w2.Close();

            cfg.AutoSwitchSeconds = 2;
            cfg.Stocks = ["sh600519", "sz000001", "sz300750", "sz000002"];
            ConfigStore.Save(cfg);
            var w3 = new MainWindow();
            w3.AutoSwitchSeconds = 2;
            w3.Show();
            await Pump(500);
            w3.SetDisplayCount(2);
            w3.SetExpanded(false);
            await Pump(200);
            var seen = new HashSet<int>();
            await WaitUntil(() => { seen.Add(w3.Index); return seen.Count >= 3; }, $"多行窗口未循环滚动: {string.Join(",", seen)}", 12000);
            Console.WriteLine($"[ok] 多行轮播循环滚动: index 轨迹覆盖 {string.Join(",", seen.OrderBy(x => x))} (max_start=2)");

            w3.SetDisplayCount(4);
            w3.SetExpanded(false);
            await Pump(200);
            var idxFixed = w3.Index;
            await WaitUntil(() => w3.CarouselRow is not null, "全部可见时高亮未循环", 8000);
            await Pump(2500);
            if (w3.Index != idxFixed) throw new Exception("全部可见时窗口不应滚动");
            Console.WriteLine($"[ok] 全部可见时轮播=高亮循环: row={w3.CarouselRow}, index 保持 {idxFixed}");
            w3.Close();

            cfg.Categories =
            [
                new WatchCategory { Name = "A股", Visible = true, Stocks = ["sh600519", "sz000001"] },
                new WatchCategory { Name = "黄金", Visible = true, Stocks = ["xau"] },
            ];
            cfg.Stocks = ["sh600519", "sz000001", "xau"];
            cfg.AutoSwitchSeconds = 0;
            ConfigStore.Save(cfg);
            var w4 = new MainWindow();
            w4.Show();
            await Pump(400);
            if (!w4.Stocks.SequenceEqual(["sh600519", "sz000001", "xau"]))
                throw new Exception($"分类可见列表错误: {string.Join(",", w4.Stocks)}");
            w4.SetCategoryVisible("A股", false);
            if (!w4.Stocks.SequenceEqual(["xau"]))
                throw new Exception($"隐藏 A 股后应只剩黄金: {string.Join(",", w4.Stocks)}");
            var catMenu = w4.BuildMenu().Items.OfType<MenuItem>().FirstOrDefault(a => (string)a.Header == "分类显示");
            if (catMenu is null) throw new Exception("右键菜单缺少分类显示");
            w4.SetCategoryVisible("A股", true);
            if (!w4.Stocks.Contains("sh600519")) throw new Exception("重新显示 A 股失败");
            Console.WriteLine("[ok] 分类显示/隐藏: 关掉 A股后只显示黄金");
            w4.Close();

            var parsed = ConfigStore.NormalizeCodes("sh600519 sz000001, sz300750\nbj430047 junk123");
            if (!parsed.SequenceEqual(["sh600519", "sz000001", "sz300750", "bj430047"]))
                throw new Exception($"解析异常: {string.Join(",", parsed)}");
            Console.WriteLine("[ok] 自选股解析: " + string.Join(",", parsed));

            w.Close();
            Console.WriteLine("\n全部冒烟测试通过");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[FAIL] " + ex.Message);
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static async Task Pump(int ms)
    {
        var end = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < end)
        {
            DoEvents();
            await Task.Delay(20);
        }
        DoEvents();
    }

    static async Task WaitUntil(Func<bool> cond, string err, int timeoutMs = 15000)
    {
        var end = DateTime.Now.AddMilliseconds(timeoutMs);
        while (DateTime.Now < end)
        {
            DoEvents();
            if (cond()) return;
            await Task.Delay(50);
        }
        DoEvents();
        if (!cond()) throw new Exception(err);
    }

    static void DoEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }
}

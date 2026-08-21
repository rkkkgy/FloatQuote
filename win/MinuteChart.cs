using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FloatQuote;

public sealed class MinuteChart : FrameworkElement
{
    const int AShareMinutes = 240;
    const int GoldMinutes = 23 * 60;

    readonly List<(int Mins, double Price, double? Avg)> _points = [];
    readonly List<KlineBar> _klines = [];
    double? _prevClose;
    string _msg = "加载中…";
    ImageSource? _fallback;
    ChartSession _session = ChartSession.AShare;
    string _periodLabel = "日K";
    int _slotCount = 60;
    int _sessionMinutes = AShareMinutes;
    Point? _hover;
    Rect _plot;
    double _x0, _y0, _pw, _ph, _ymax, _ymin, _yRange, _candleH, _slot;

    public IReadOnlyList<(int Mins, double Price, double? Avg)> Points => _points;
    public ImageSource? Fallback => _fallback;

    public MinuteChart()
    {
        MinWidth = 240;
        MinHeight = 200;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MouseMove += OnHoverMove;
        MouseLeave += (_, _) => ClearHover();
        Loaded += (_, _) => ApplyCache();
    }

    void ApplyCache()
    {
        if (_klines.Count > 0)
        {
            CacheMode = null;
            return;
        }
        var scale = Math.Max(2.0, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        CacheMode = new BitmapCache
        {
            RenderAtScale = scale,
            SnapsToDevicePixels = true,
            EnableClearType = true,
        };
    }

    public void SetData(IEnumerable<MinutePoint> points, double? prevClose, ChartSession session = ChartSession.AShare)
    {
        _fallback = null;
        _session = session;
        _sessionMinutes = session == ChartSession.Gold ? GoldMinutes : AShareMinutes;
        _klines.Clear();
        _points.Clear();
        foreach (var p in points)
        {
            try
            {
                var ts = p.Time.PadLeft(4, '0');
                var hh = int.Parse(ts[..2], CultureInfo.InvariantCulture);
                var mm = int.Parse(ts[2..], CultureInfo.InvariantCulture);
                int mins;
                if (session == ChartSession.Gold)
                {
                    mins = hh * 60 + mm - 6 * 60;
                    if (mins < 0) mins += 24 * 60;
                }
                else
                {
                    mins = hh * 60 + mm - 9 * 60 - 30;
                    if (mins > 120) mins -= 90;
                }
                mins = Math.Clamp(mins, 0, _sessionMinutes);
                var avg = p.Avg is 0 ? null : p.Avg;
                _points.Add((mins, p.Price, avg));
            }
            catch (FormatException) { }
            catch (ArgumentOutOfRangeException) { }
        }
        _prevClose = prevClose;
        if (_points.Count == 0)
            _msg = "暂无分时数据（非交易时段）";
        ApplyCache();
        InvalidateVisual();
    }

    public void SetKline(IEnumerable<KlineBar> bars, string periodLabel, int slotCount = 60)
    {
        _fallback = null;
        _periodLabel = string.IsNullOrEmpty(periodLabel) ? "K线" : periodLabel;
        _slotCount = Math.Clamp(slotCount, 10, 200);
        _points.Clear();
        _prevClose = null;
        _klines.Clear();
        var list = bars.Where(b => b.High > 0 && b.Low > 0 && b.High >= b.Low).ToList();
        if (list.Count > _slotCount)
            list = list.TakeLast(_slotCount).ToList();
        _klines.AddRange(list);
        _msg = _klines.Count == 0 ? "暂无K线数据" : "加载中…";
        ApplyCache();
        InvalidateVisual();
    }

    public void SetLoading()
    {
        _points.Clear();
        _klines.Clear();
        _prevClose = null;
        _fallback = null;
        _msg = "加载中…";
        InvalidateVisual();
    }

    public void ShowImage(byte[] data)
    {
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(data);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            _fallback = img;
            _points.Clear();
            _klines.Clear();
            InvalidateVisual();
        }
        catch
        {
            SetLoading();
        }
    }

    double Dip
    {
        get
        {
            var d = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return d > 0 ? d : 1.0;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var dip = Dip;
        const double left = 44, right = 42, top = 8, bottom = 22;
        var pw = w - left - right;
        var ph = h - top - bottom;
        if (pw <= 8 || ph <= 8) return;

        var ui = new Typeface(Theme.UiFont);
        var mono = new Typeface(Theme.MonoFont);
        var text = BrushOf(Theme.TextSub);
        var plotBg = BrushOf(Color.FromRgb(20, 24, 32));
        var gridPen = PenOf(Color.FromRgb(52, 58, 72), 1);
        var axisPen = PenOf(Color.FromRgb(72, 80, 98), 1);

        var plot = new Rect(Snap(left), Snap(top), Math.Max(1, pw), Math.Max(1, ph));
        _plot = plot;
        _x0 = plot.X; _y0 = plot.Y; _pw = pw; _ph = ph;
        dc.DrawRoundedRectangle(plotBg, axisPen, plot, 3, 3);

        if (_fallback is not null)
        {
            var img = _fallback;
            var scale = Math.Min(pw / img.Width, ph / img.Height);
            var dw = img.Width * scale;
            var dh = img.Height * scale;
            dc.DrawImage(img, new Rect(left + (pw - dw) / 2, top + (ph - dh) / 2, dw, dh));
            return;
        }

        if (_klines.Count > 0)
        {
            DrawKline(dc, plot, pw, ph, ui, mono, text, gridPen, dip);
            return;
        }

        if (_points.Count == 0)
        {
            DrawCentered(dc, _msg, ui, 13, text, new Rect(0, 0, w, h));
            return;
        }

        var lastMins = _points[^1].Mins;
        var xSpan = _session == ChartSession.Gold
            ? Math.Clamp(Math.Max(lastMins + 25, 180), 180, _sessionMinutes)
            : AShareMinutes;

        var prices = _points.Where(p => p.Price > 0).Select(p => p.Price).ToList();
        if (prices.Count == 0)
        {
            DrawCentered(dc, "暂无有效分时价格", ui, 13, text, new Rect(0, 0, w, h));
            return;
        }
        var lastPrice = prices[^1];
        var prev = _prevClose is > 0 ? _prevClose.Value : lastPrice;

        var peak = prices.Append(prev).Max();
        var trough = prices.Append(prev).Min();
        var dev = Math.Max(peak - prev, prev - trough);
        var minDev = Math.Max(prev * 0.008, 0.02);
        if (dev < minDev) dev = minDev;
        var ymax = prev + dev * 1.08;
        var ymin = prev - dev * 1.08;
        var yRange = ymax - ymin;
        if (yRange <= 0) yRange = 0.02;
        _ymax = ymax; _ymin = ymin; _yRange = yRange; _candleH = ph; _slot = 0;

        double x0 = plot.X, y0 = plot.Y;
        Point Map(double mins, double price)
            => new(
                x0 + pw * Math.Clamp(mins, 0, xSpan) / xSpan,
                y0 + ph * (ymax - price) / yRange);

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var frac in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var yy = Snap(y0 + ph * frac);
            dc.DrawLine(gridPen, new Point(x0, yy), new Point(x0 + pw, yy));
        }
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            var xx = Snap(x0 + pw * frac);
            dc.DrawLine(gridPen, new Point(xx, y0), new Point(xx, y0 + ph));
        }

        var prevY = Map(0, prev).Y;
        dc.DrawLine(DashPen(Theme.Yellow, 1.2), new Point(x0, prevY), new Point(x0 + pw, prevY));

        var up = lastPrice >= prev;
        var main = up ? Theme.Red : Theme.Green;
        var mainBrush = BrushOf(main);

        var avgPts = _points.Where(p => p.Avg is > 0 && p.Price > 0
            && Math.Abs(p.Avg.Value - p.Price) / p.Price <= 0.25).ToList();
        if (avgPts.Count > 1)
        {
            dc.DrawGeometry(null, DashPen(Color.FromRgb(240, 210, 120), 1.4, dotted: true),
                Poly(avgPts.Select(p => Map(p.Mins, p.Avg!.Value))));
        }

        var pricePts = _points.Select(p => Map(p.Mins, p.Price)).ToList();
        if (pricePts.Count >= 1)
        {
            var fill = new PathGeometry();
            var fig = new PathFigure { StartPoint = pricePts[0], IsClosed = true };
            foreach (var pt in pricePts.Skip(1))
                fig.Segments.Add(new LineSegment(pt, true));
            fig.Segments.Add(new LineSegment(new Point(pricePts[^1].X, y0 + ph), false));
            fig.Segments.Add(new LineSegment(new Point(pricePts[0].X, y0 + ph), false));
            fill.Figures.Add(fig);
            var fillBrush = new LinearGradientBrush(
                Color.FromArgb(90, main.R, main.G, main.B),
                Color.FromArgb(12, main.R, main.G, main.B),
                new Point(0, 0), new Point(0, 1));
            fillBrush.Freeze();
            dc.DrawGeometry(fillBrush, null, fill);
            dc.DrawGeometry(null, SolidPen(main, 2.2), Poly(pricePts));
            dc.DrawEllipse(mainBrush, null, pricePts[^1], 3.2, 3.2);
        }
        dc.Pop();

        var yellow = BrushOf(Theme.Yellow);
        DrawAxisLabel(dc, $"{ymax:0.00}", mono, x0 - 6, y0, true, text);
        DrawAxisLabel(dc, $"{prev:0.00}", mono, x0 - 6, prevY - 7, true, yellow);
        DrawAxisLabel(dc, $"{ymin:0.00}", mono, x0 - 6, y0 + ph - 13, true, text);

        DrawAxisLabel(dc, Pct(ymax, prev), mono, x0 + pw + 6, y0, false, BrushOf(Theme.Red));
        DrawAxisLabel(dc, "0.00%", mono, x0 + pw + 6, prevY - 7, false, yellow);
        DrawAxisLabel(dc, Pct(ymin, prev), mono, x0 + pw + 6, y0 + ph - 13, false, BrushOf(Theme.Green));

        var xLabels = _session == ChartSession.Gold
            ? new (string Text, double Frac)[]
            {
                (ClockGold(0), 0),
                (ClockGold((int)(xSpan * 0.5)), 0.5),
                (ClockGold((int)xSpan), 1),
            }
            : new (string Text, double Frac)[]
            {
                ("09:30", 0),
                ("11:30", 0.5),
                ("15:00", 1),
            };
        foreach (var (label, frac) in xLabels)
        {
            var ft = Fmt(label, ui, 11, text, dip);
            var xx = x0 + pw * frac - ft.Width / 2;
            xx = Math.Clamp(xx, x0, x0 + pw - ft.Width);
            dc.DrawText(ft, new Point(xx, y0 + ph + 3));
        }

        var tag = Fmt($"{lastPrice:0.00}", mono, 12, Brushes.White, dip);
        var tagW = tag.Width + 10;
        const double tagH = 16;
        var last = pricePts[^1];
        var bx = Math.Clamp(last.X + 6, x0, x0 + pw - tagW);
        var by = Math.Clamp(last.Y - tagH - 4, y0 + 2, y0 + ph - tagH - 2);
        dc.DrawRoundedRectangle(mainBrush, null, new Rect(bx, by, tagW, tagH), 3, 3);
        dc.DrawText(tag, new Point(bx + 5, by + (tagH - tag.Height) / 2));
        DrawCrosshair(dc, ui, mono, dip, kline: false);
    }

    void DrawKline(DrawingContext dc, Rect plot, double pw, double ph, Typeface ui, Typeface mono, Brush text, Pen gridPen, double dip)
    {
        var n = _klines.Count;
        var slots = Math.Max(_slotCount, 1);
        var ymax = _klines.Max(b => b.High);
        var ymin = _klines.Min(b => b.Low);
        var pad = Math.Max((ymax - ymin) * 0.06, Math.Max(ymax * 0.002, 0.01));
        ymax += pad;
        ymin = Math.Max(0, ymin - pad);
        var yRange = ymax - ymin;
        if (yRange <= 0) yRange = 0.02;
        _ymax = ymax; _ymin = ymin; _yRange = yRange;

        double x0 = plot.X, y0 = plot.Y;
        var volH = ph * 0.22;
        var gap = 3;
        var candleH = ph - volH - gap;
        if (candleH < 40)
        {
            volH = 0;
            gap = 0;
            candleH = ph;
        }
        var maxVol = _klines.Select(b => b.Volume ?? 0).DefaultIfEmpty(0).Max();

        double Yp(double price) => y0 + candleH * (ymax - price) / yRange;
        var slot = pw / slots;
        _slot = slot;
        _candleH = candleH;
        double Xc(int i) => x0 + slot * (i + 0.5);
        var bodyW = Math.Clamp(slot * 0.68, 1.0, 9.0);

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var frac in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var yy = Snap(y0 + candleH * frac);
            dc.DrawLine(gridPen, new Point(x0, yy), new Point(x0 + pw, yy));
        }
        foreach (var frac in new[] { 0.0, 0.5, 1.0 })
        {
            var xx = Snap(x0 + pw * frac);
            dc.DrawLine(gridPen, new Point(xx, y0), new Point(xx, y0 + ph));
        }

        for (var i = 0; i < n; i++)
        {
            var b = _klines[i];
            var up = b.Close >= b.Open;
            var color = up ? Theme.Red : Theme.Green;
            var cx = Xc(i);
            var yHigh = Yp(b.High);
            var yLow = Yp(b.Low);
            var yOpen = Yp(b.Open);
            var yClose = Yp(b.Close);
            dc.DrawLine(PenOf(color, 1), new Point(cx, yHigh), new Point(cx, yLow));
            var top = Math.Min(yOpen, yClose);
            var bot = Math.Max(yOpen, yClose);
            var bh = Math.Max(1.2, bot - top);
            var rect = new Rect(cx - bodyW / 2, top, bodyW, bh);
            dc.DrawRectangle(BrushOf(color), null, rect);

            if (volH > 0 && maxVol > 0 && b.Volume is > 0)
            {
                var vh = volH * b.Volume.Value / maxVol;
                var vy = y0 + candleH + gap + (volH - vh);
                dc.DrawRectangle(BrushOf(Color.FromArgb(140, color.R, color.G, color.B)), null,
                    new Rect(cx - bodyW / 2, vy, bodyW, Math.Max(1, vh)));
            }
        }
        dc.Pop();

        DrawAxisLabel(dc, $"{ymax:0.00}", mono, x0 - 6, y0, true, text);
        DrawAxisLabel(dc, $"{(ymax + ymin) / 2:0.00}", mono, x0 - 6, y0 + candleH / 2 - 7, true, text);
        DrawAxisLabel(dc, $"{ymin:0.00}", mono, x0 - 6, y0 + candleH - 13, true, text);

        var last = _klines[^1];
        var refClose = n >= 2 ? _klines[^2].Close : last.Open;
        var lastPct = refClose == 0 ? "--" : $"{(last.Close - refClose) / refClose * 100:+0.00;-0.00}%";
        DrawAxisLabel(dc, lastPct, mono, x0 + pw + 6, y0, false, BrushOf(last.Close >= refClose ? Theme.Red : Theme.Green));

        string ShortDate(string d)
        {
            if (d.Length >= 10) return d[5..10];
            return d;
        }
        var labels = new List<(string Text, double Frac)>
        {
            (ShortDate(_klines[0].Date), (0 + 0.5) / slots),
        };
        if (n >= 3)
            labels.Add((ShortDate(_klines[n / 2].Date), (n / 2 + 0.5) / slots));
        if (n >= 2)
            labels.Add((ShortDate(last.Date), (n - 0.5) / slots));
        foreach (var (label, frac) in labels)
        {
            var ft = Fmt(label, ui, 11, text, dip);
            var xx = x0 + pw * frac - ft.Width / 2;
            xx = Math.Clamp(xx, x0, x0 + pw - ft.Width);
            dc.DrawText(ft, new Point(xx, y0 + ph + 3));
        }

        var title = Fmt($"{_periodLabel}  {_klines.Count}/{slots}根  ·  滚轮切换周期", ui, 11, BrushOf(Theme.Yellow), dip);
        dc.DrawText(title, new Point(x0 + 6, y0 + 4));
        DrawCrosshair(dc, ui, mono, dip, kline: true);
    }

    void OnHoverMove(object sender, MouseEventArgs e)
    {
        if (_klines.Count == 0 && _points.Count == 0) return;
        var p = e.GetPosition(this);
        if (_plot.Width < 8 || !_plot.Contains(p))
        {
            ClearHover();
            return;
        }
        _hover = p;
        Cursor = Cursors.Cross;
        InvalidateVisual();
    }

    void ClearHover()
    {
        if (_hover is null) return;
        _hover = null;
        Cursor = Cursors.Arrow;
        InvalidateVisual();
    }

    int HoverBarIndex()
    {
        if (_hover is not Point p || _klines.Count == 0 || _slot <= 0) return -1;
        var i = (int)Math.Floor((p.X - _x0) / _slot);
        return i >= 0 && i < _klines.Count ? i : -1;
    }

    double? HoverPrice()
    {
        if (_hover is not Point p || _yRange <= 0 || _candleH <= 0) return null;
        if (p.Y < _y0 || p.Y > _y0 + _candleH) return null;
        return _ymax - (p.Y - _y0) / _candleH * _yRange;
    }

    void DrawCrosshair(DrawingContext dc, Typeface ui, Typeface mono, double dip, bool kline)
    {
        if (_hover is not Point hp || !_plot.Contains(hp)) return;
        var x = Math.Clamp(hp.X, _x0, _x0 + _pw);
        var y = Math.Clamp(hp.Y, _y0, _y0 + _ph);
        var hair = DashPen(Color.FromArgb(200, 180, 190, 210), 1.0);
        var bar = kline ? HoverBarIndex() : -1;
        if (bar >= 0)
        {
            x = _x0 + _slot * (bar + 0.5);
            var band = new Rect(_x0 + _slot * bar, _y0, Math.Max(1, _slot), _ph);
            dc.DrawRectangle(BrushOf(Color.FromArgb(28, 245, 194, 66)), null, band);
        }
        dc.DrawLine(hair, new Point(x, _y0), new Point(x, _y0 + _ph));
        dc.DrawLine(hair, new Point(_x0, y), new Point(_x0 + _pw, y));

        var price = HoverPrice();
        if (price is double pv)
        {
            var tag = Fmt($"{pv:0.00}", mono, 11, Brushes.White, dip);
            var tw = tag.Width + 8;
            var th = 15;
            var tx = _x0 + _pw + 3;
            var ty = Math.Clamp(y - th / 2, _y0, _y0 + _ph - th);
            dc.DrawRoundedRectangle(BrushOf(Color.FromRgb(62, 107, 79)), null, new Rect(tx, ty, tw, th), 2, 2);
            dc.DrawText(tag, new Point(tx + 4, ty + (th - tag.Height) / 2));
        }

        if (kline && bar >= 0)
            DrawKlineCard(dc, ui, mono, dip, bar, hp);
        else if (!kline && _points.Count > 0)
        {
            var lastMins = _points[^1].Mins;
            var xSpan = _session == ChartSession.Gold
                ? Math.Clamp(Math.Max(lastMins + 25, 180), 180, _sessionMinutes)
                : AShareMinutes;
            var mins = (int)Math.Clamp((hp.X - _x0) / _pw * xSpan, 0, xSpan);
            var time = _session == ChartSession.Gold ? ClockGold(mins) : ClockAShare(mins);
            var ft = Fmt(time, ui, 11, Brushes.White, dip);
            var tw = ft.Width + 8;
            var tx = Math.Clamp(x - tw / 2, _x0, _x0 + _pw - tw);
            dc.DrawRoundedRectangle(BrushOf(Color.FromRgb(44, 49, 64)), null, new Rect(tx, _y0 + _ph + 2, tw, 14), 2, 2);
            dc.DrawText(ft, new Point(tx + 4, _y0 + _ph + 3));
        }
    }

    void DrawKlineCard(DrawingContext dc, Typeface ui, Typeface mono, double dip, int i, Point mouse)
    {
        var b = _klines[i];
        var prev = i > 0 ? _klines[i - 1].Close : b.Open;
        var chg = b.Close - prev;
        var chgPct = prev == 0 ? 0 : chg / prev * 100;
        var amp = prev == 0 ? 0 : (b.High - b.Low) / prev * 100;
        var up = b.Close >= prev;
        var accent = BrushOf(up ? Theme.Red : Theme.Green);
        var main = BrushOf(Theme.TextMain);
        var sub = BrushOf(Theme.TextSub);

        string Row(string k, string v) => $"{k}  {v}";
        var lines = new (string Text, Brush Brush)[]
        {
            (b.Date, main),
            (Row("开盘", $"{b.Open:0.00}"), main),
            (Row("最高", $"{b.High:0.00}"), BrushOf(Theme.Red)),
            (Row("最低", $"{b.Low:0.00}"), BrushOf(Theme.Green)),
            (Row("收盘", $"{b.Close:0.00}"), accent),
            (Row("涨跌", $"{chg:+0.00;-0.00}  {chgPct:+0.00;-0.00}%"), accent),
            (Row("振幅", $"{amp:0.00}%"), main),
            (Row("成交量", FmtVol(b.Volume)), sub),
            (Row("成交额", FmtAmt(b.Amount)), sub),
        };

        var fts = lines.Select(l => Fmt(l.Text, l.Text == b.Date ? ui : mono, l.Text == b.Date ? 11 : 11, l.Brush, dip)).ToList();
        var cardW = Math.Max(138, fts.Max(t => t.Width) + 16);
        var lineH = 16.0;
        var cardH = 8 + lines.Length * lineH;
        var leftSide = mouse.X > _x0 + _pw * 0.55;
        var cx = leftSide ? _x0 + 6 : _x0 + _pw - cardW - 6;
        var cy = _y0 + 22;
        if (cy + cardH > _y0 + _ph - 4)
            cy = Math.Max(_y0 + 4, _y0 + _ph - cardH - 4);

        dc.DrawRoundedRectangle(
            BrushOf(Color.FromArgb(230, 24, 28, 38)),
            PenOf(Color.FromRgb(72, 80, 98), 1),
            new Rect(cx, cy, cardW, cardH), 4, 4);
        for (var r = 0; r < lines.Length; r++)
            dc.DrawText(fts[r], new Point(cx + 8, cy + 4 + r * lineH));
    }

    static string FmtVol(double? v)
    {
        if (v is null) return "--";
        if (v >= 1e8) return $"{v.Value / 1e8:0.00}亿手";
        if (v >= 1e4) return $"{v.Value / 1e4:0.00}万手";
        return $"{v.Value:0}手";
    }

    static string FmtAmt(double? v)
    {
        if (v is null) return "--";
        if (v >= 1e8) return $"{v.Value / 1e8:0.00}亿";
        if (v >= 1e4) return $"{v.Value / 1e4:0.00}万";
        return $"{v.Value:0}";
    }

    static string ClockAShare(int mins)
    {
        mins = Math.Clamp(mins, 0, AShareMinutes);
        var abs = 9 * 60 + 30 + mins;
        if (mins > 120) abs += 90;
        return $"{abs / 60:00}:{abs % 60:00}";
    }

    static string Pct(double price, double prev)
    {
        if (prev == 0) return "--";
        var p = (price - prev) / prev * 100;
        return $"{p:+0.00;-0.00}%";
    }

    static string ClockGold(int mins)
    {
        var abs = (6 * 60 + Math.Max(0, mins)) % (24 * 60);
        return $"{abs / 60:00}:{abs % 60:00}";
    }

    static double Snap(double v) => Math.Round(v) + 0.5;

    static SolidColorBrush BrushOf(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Pen PenOf(Color c, double width)
    {
        var p = new Pen(BrushOf(c), width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        p.Freeze();
        return p;
    }

    static Pen SolidPen(Color c, double width)
    {
        var p = new Pen(BrushOf(c), width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        p.Freeze();
        return p;
    }

    static Pen DashPen(Color c, double width, bool dotted = false)
    {
        var p = new Pen(BrushOf(c), width)
        {
            DashStyle = dotted ? DashStyles.Dot : DashStyles.Dash,
            StartLineCap = PenLineCap.Flat,
            EndLineCap = PenLineCap.Flat,
        };
        p.Freeze();
        return p;
    }

    static StreamGeometry Poly(IEnumerable<Point> pts)
    {
        var g = new StreamGeometry();
        using var ctx = g.Open();
        var first = true;
        foreach (var pt in pts)
        {
            if (first) { ctx.BeginFigure(pt, false, false); first = false; }
            else ctx.LineTo(pt, true, true);
        }
        g.Freeze();
        return g;
    }

    FormattedText Fmt(string text, Typeface tf, double size, Brush brush, double dip)
        => new(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight, tf, size, brush, dip);

    void DrawCentered(DrawingContext dc, string text, Typeface tf, double size, Brush brush, Rect rect)
    {
        var ft = Fmt(text, tf, size, brush, Dip);
        dc.DrawText(ft, new Point(rect.X + (rect.Width - ft.Width) / 2, rect.Y + (rect.Height - ft.Height) / 2));
    }

    void DrawAxisLabel(DrawingContext dc, string text, Typeface tf, double x, double y, bool rightAlign, Brush brush)
    {
        var ft = Fmt(text, tf, 11, brush, Dip);
        var px = rightAlign ? x - ft.Width : x;
        dc.DrawText(ft, new Point(px, y));
    }
}

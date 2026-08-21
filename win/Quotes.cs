using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatQuote;

public sealed class Quote
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "--";
    public double? Price { get; set; }
    public double? PrevClose { get; set; }
    public double? Open { get; set; }
    public double? High { get; set; }
    public double? Low { get; set; }
    public double? Change { get; set; }
    public double? ChangePct { get; set; }
    public double? Volume { get; set; }
    public double? Amount { get; set; }
    public double? Turnover { get; set; }
    public double? Pe { get; set; }
    public string Time { get; set; } = "";

    public bool Ok => Price is not null;

    public string ColorKey
    {
        get
        {
            if (ChangePct is null || Math.Abs(ChangePct.Value) < 1e-9)
                return "gray";
            return ChangePct > 0 ? "red" : "green";
        }
    }
}

public readonly record struct MinutePoint(string Time, double Price, double? Avg);

public readonly record struct KlineBar(string Date, double Open, double Close, double High, double Low, double? Volume, double? Amount = null);

public enum ChartSession { AShare, Gold }

public enum ChartKind { Minute, Kline }

public enum KlinePeriod { M1, M5, M15, M30, M60, Day, Week, Month }

public static class KlinePeriods
{
    public static readonly KlinePeriod[] All =
        [KlinePeriod.M1, KlinePeriod.M5, KlinePeriod.M15, KlinePeriod.M30, KlinePeriod.M60, KlinePeriod.Day, KlinePeriod.Week, KlinePeriod.Month];

    public static string Label(KlinePeriod p) => p switch
    {
        KlinePeriod.M1 => "1分",
        KlinePeriod.M5 => "5分",
        KlinePeriod.M15 => "15分",
        KlinePeriod.M30 => "30分",
        KlinePeriod.M60 => "60分",
        KlinePeriod.Day => "日K",
        KlinePeriod.Week => "周K",
        KlinePeriod.Month => "月K",
        _ => "日K",
    };

    public static string Key(KlinePeriod p) => p switch
    {
        KlinePeriod.M1 => "m1",
        KlinePeriod.M5 => "m5",
        KlinePeriod.M15 => "m15",
        KlinePeriod.M30 => "m30",
        KlinePeriod.M60 => "m60",
        KlinePeriod.Week => "week",
        KlinePeriod.Month => "month",
        _ => "day",
    };

    public static KlinePeriod Parse(string? s) => s?.Trim().ToLowerInvariant() switch
    {
        "m1" or "1" => KlinePeriod.M1,
        "m5" or "5" => KlinePeriod.M5,
        "m15" or "15" => KlinePeriod.M15,
        "m30" or "30" => KlinePeriod.M30,
        "m60" or "60" => KlinePeriod.M60,
        "week" => KlinePeriod.Week,
        "month" => KlinePeriod.Month,
        _ => KlinePeriod.Day,
    };

    public static int Klt(KlinePeriod p) => p switch
    {
        KlinePeriod.M1 => 1,
        KlinePeriod.M5 => 5,
        KlinePeriod.M15 => 15,
        KlinePeriod.M30 => 30,
        KlinePeriod.M60 => 60,
        KlinePeriod.Week => 102,
        KlinePeriod.Month => 103,
        _ => 101,
    };

    public static int Limit(KlinePeriod p) => p switch
    {
        KlinePeriod.M1 => 240,
        KlinePeriod.M5 => 120,
        KlinePeriod.M15 => 96,
        KlinePeriod.Month => 48,
        _ => 80,
    };
}

public static class QuoteClient
{
    const string QuoteUrl = "https://qt.gtimg.cn/q={0}";
    const string MinuteUrl = "https://web.ifzq.gtimg.cn/appstock/app/minute/query?code={0}";
    const string MinuteImgUrl = "https://image.sinajs.cn/newchart/min/n/{0}.gif";
    const string SearchUrl = "https://smartbox.gtimg.cn/s3/?v=2&q={0}&t=all";
    const string EmQuoteUrl = "https://push2.eastmoney.com/api/qt/stock/get?secid={0}&fields=f43,f44,f45,f46,f57,f58,f60,f169,f170";
    const string EmTrendUrl = "https://push2his.eastmoney.com/api/qt/stock/trends2/get?secid={0}&fields1=f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13&fields2=f51,f52,f53,f54,f55,f56,f57,f58&iscr=0&ndays=1";
    const string EmKlineUrl = "https://push2his.eastmoney.com/api/qt/stock/kline/get?secid={0}&klt={1}&fqt=1&lmt={2}&end=20500101&fields1=f1,f2,f3,f4,f5,f6&fields2=f51,f52,f53,f54,f55,f56,f57";

    static readonly Dictionary<string, (string SecId, string Name, int Decimals)> GoldMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xau"] = ("122.XAU", "伦敦金", 2),
        ["gc"] = ("101.GC00Y", "COMEX金", 1),
    };

    static readonly HttpClient Http = CreateClient();
    static readonly Regex QuoteRe = new(@"v_(\w+)=""([^""]*)""", RegexOptions.Compiled);
    static readonly Regex HintRe = new(@"v_hint=""([^""]*)""", RegexOptions.Compiled);

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        c.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        return c;
    }

    public static bool IsGold(string code) => GoldMap.ContainsKey(ConfigStore.CanonicalCode(code));

    public static ChartSession SessionOf(string code) => IsGold(code) ? ChartSession.Gold : ChartSession.AShare;

    public static bool IsTradingTime(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        if ((int)t.DayOfWeek is 0 or 6)
            return false;
        var hm = t.Hour * 60 + t.Minute;
        return (9 * 60 + 15) <= hm && hm <= (11 * 60 + 30)
            || 13 * 60 <= hm && hm <= 15 * 60;
    }

    public static bool IsGoldMarketOpen(DateTime? now = null)
    {
        var t = now ?? DateTime.Now;
        var hm = t.Hour * 60 + t.Minute;
        if (t.DayOfWeek == DayOfWeek.Saturday && hm >= 6 * 60) return false;
        if (t.DayOfWeek == DayOfWeek.Sunday && hm < 6 * 60) return false;
        return true;
    }

    public static bool ShouldRefresh(IEnumerable<string> codes, DateTime? now = null)
        => IsTradingTime(now) || (codes.Any(IsGold) && IsGoldMarketOpen(now));

    public static string FmtPrice(double? v) => v is null ? "--" : v.Value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string FmtPct(double? v) => v is null ? "--" : $"{v.Value:+0.00;-0.00}%";

    public static string FmtVolume(double? hands)
    {
        if (hands is null) return "--";
        if (hands >= 1e8) return $"{hands.Value / 1e8:0.00}亿手";
        if (hands >= 1e4) return $"{hands.Value / 1e4:0.00}万手";
        return $"{hands.Value:0}手";
    }

    public static string FmtAmount(double? wan)
    {
        if (wan is null) return "--";
        if (wan >= 1e4) return $"{wan.Value / 1e4:0.00}亿";
        return $"{wan.Value:0}万";
    }

    public static Dictionary<string, Quote> FetchQuotes(IEnumerable<string> codes)
        => FetchQuotesAsync(codes).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<Dictionary<string, Quote>> FetchQuotesAsync(IEnumerable<string> codes, CancellationToken ct = default)
    {
        var list = codes.Select(ConfigStore.CanonicalCode).ToList();
        var outDict = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        var equity = list.Where(c => !IsGold(c)).ToList();
        var gold = list.Where(IsGold).ToList();
        if (equity.Count > 0)
        {
            foreach (var kv in await FetchEquityQuotesAsync(equity, ct).ConfigureAwait(false))
                outDict[kv.Key] = kv.Value;
        }
        foreach (var code in gold)
        {
            var q = await FetchGoldQuoteAsync(code, ct).ConfigureAwait(false);
            if (q is not null)
                outDict[code] = q;
        }
        return outDict;
    }

    static async Task<Dictionary<string, Quote>> FetchEquityQuotesAsync(List<string> codes, CancellationToken ct)
    {
        var text = await HttpGetAsync(string.Format(QuoteUrl, string.Join(",", codes)), ct).ConfigureAwait(false);
        var outDict = new Dictionary<string, Quote>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in QuoteRe.Matches(text))
        {
            var code = m.Groups[1].Value;
            var f = m.Groups[2].Value.Split('~');
            if (f.Length < 35)
                continue;
            var q = new Quote { Code = code, Name = string.IsNullOrEmpty(f[1]) ? "--" : f[1] };
            q.Price = F(f[3]);
            q.PrevClose = F(f[4]);
            q.Open = F(f[5]);
            q.Volume = F(f[6]);
            q.High = F(f[33]);
            q.Low = F(f[34]);
            q.Change = F(f[31]);
            q.ChangePct = F(f[32]);
            if (f.Length > 37) q.Amount = F(f[37]);
            if (f.Length > 38) q.Turnover = F(f[38]);
            if (f.Length > 39) q.Pe = F(f[39]);
            if (f.Length > 30) q.Time = f[30];
            outDict[code] = q;
        }
        return outDict;
    }

    static async Task<Quote?> FetchGoldQuoteAsync(string code, CancellationToken ct)
    {
        if (!GoldMap.TryGetValue(code, out var meta)) return null;
        var json = await HttpGetUtf8Async(string.Format(EmQuoteUrl, meta.SecId), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return null;
        var scale = Math.Pow(10, meta.Decimals);
        var q = new Quote
        {
            Code = code,
            Name = meta.Name,
            Price = Scaled(data, "f43", scale),
            High = Scaled(data, "f44", scale),
            Low = Scaled(data, "f45", scale),
            Open = Scaled(data, "f46", scale),
            PrevClose = Scaled(data, "f60", scale),
            Change = Scaled(data, "f169", scale),
            ChangePct = Scaled(data, "f170", 100),
        };
        return q.Price is null ? null : q;
    }

    public static async Task<(List<MinutePoint> Points, string Date)> FetchMinuteAsync(string code, CancellationToken ct = default)
    {
        code = ConfigStore.CanonicalCode(code);
        if (IsGold(code))
            return await FetchGoldMinuteAsync(code, ct).ConfigureAwait(false);

        var text = await HttpGetAsync(string.Format(MinuteUrl, code), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var data = doc.RootElement.GetProperty("data").GetProperty(code).GetProperty("data");
        var date = data.TryGetProperty("date", out var d) ? d.ToString() : "";
        var points = new List<MinutePoint>();
        if (!data.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (points, date);

        foreach (var p in arr.EnumerateArray())
        {
            string t;
            double? price, vol, amt, avg = null;
            if (p.ValueKind == JsonValueKind.String)
            {
                var parts = p.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3) continue;
                t = parts[0].PadLeft(4, '0');
                price = F(parts[1]);
                vol = F(parts[2]);
                amt = parts.Length > 3 ? F(parts[3]) : null;
            }
            else if (p.ValueKind == JsonValueKind.Array)
            {
                var items = p.EnumerateArray().ToList();
                if (items.Count < 3) continue;
                t = items[0].ToString().PadLeft(4, '0');
                price = Num(items[1]);
                vol = Num(items[2]);
                amt = items.Count > 3 ? Num(items[3]) : null;
                if (items.Count > 4) avg = Num(items[4]);
            }
            else continue;

            if (price is not > 0) continue;
            avg = InferAvg(price.Value, vol, amt, avg);
            points.Add(new MinutePoint(t, price.Value, avg));
        }
        return (points, date);
    }

    static async Task<(List<MinutePoint> Points, string Date)> FetchGoldMinuteAsync(string code, CancellationToken ct)
    {
        var meta = GoldMap[code];
        var json = await HttpGetUtf8Async(string.Format(EmTrendUrl, meta.SecId), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var points = new List<MinutePoint>();
        var date = "";
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return (points, date);
        if (data.TryGetProperty("preClose", out var pc) || data.TryGetProperty("prePrice", out pc))
            _ = pc;
        if (!data.TryGetProperty("trends", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return (points, date);
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            var parts = s.Split(',');
            if (parts.Length < 3) continue;
            if (!DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)
                && !DateTime.TryParse(parts[0], CultureInfo.CurrentCulture, DateTimeStyles.None, out ts))
                continue;
            if (date.Length == 0)
                date = ts.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var close = F(parts[2]);
            if (close is not > 0) continue;
            var avg = parts.Length > 7 ? F(parts[7]) : null;
            var vol = parts.Length > 5 ? F(parts[5]) : null;
            var amt = parts.Length > 6 ? F(parts[6]) : null;
            avg = InferAvg(close.Value, vol, amt, avg);
            points.Add(new MinutePoint(ts.ToString("HHmm", CultureInfo.InvariantCulture), close.Value, avg));
        }
        return (points, date);
    }

    public static List<KlineBar> FetchKline(string code, KlinePeriod period)
        => FetchKlineAsync(code, period).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<List<KlineBar>> FetchKlineAsync(string code, KlinePeriod period, int count = 0, CancellationToken ct = default)
    {
        code = ConfigStore.CanonicalCode(code);
        var lmt = Math.Clamp(count > 0 ? count : KlinePeriods.Limit(period), ConfigStore.MinKlineCount, 240);
        try
        {
            var json = await HttpGetUtf8Async(
                string.Format(EmKlineUrl, EmSecId(code), KlinePeriods.Klt(period), lmt), ct).ConfigureAwait(false);
            var bars = ParseEmKlines(json);
            if (bars.Count > 0) return bars;
        }
        catch { /* fallback */ }
        try
        {
            return await FetchTencentKlineAsync(code, period, lmt, ct).ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    static List<KlineBar> ParseEmKlines(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var bars = new List<KlineBar>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return bars;
        if (!data.TryGetProperty("klines", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return bars;
        foreach (var item in arr.EnumerateArray())
        {
            var s = item.GetString();
            if (string.IsNullOrEmpty(s)) continue;
            var bar = ParseBar(s.Split(','), dateIdx: 0, open: 1, close: 2, high: 3, low: 4, vol: 5);
            if (bar is { } b) bars.Add(b);
        }
        return bars;
    }

    static async Task<List<KlineBar>> FetchTencentKlineAsync(string code, KlinePeriod period, int lmt, CancellationToken ct)
    {
        if (IsGold(code)) return [];
        var (url, keys) = period switch
        {
            KlinePeriod.Day => ($"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={code},day,,,{lmt},qfq",
                (string[])["qfqday", "day"]),
            KlinePeriod.Week => ($"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={code},week,,,{lmt},qfq",
                ["qfqweek", "week"]),
            KlinePeriod.Month => ($"https://web.ifzq.gtimg.cn/appstock/app/fqkline/get?param={code},month,,,{lmt},qfq",
                ["qfqmonth", "month"]),
            KlinePeriod.M1 => ($"https://web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={code},m1,,{lmt}", ["m1"]),
            KlinePeriod.M5 => ($"https://web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={code},m5,,{lmt}", ["m5"]),
            KlinePeriod.M15 => ($"https://web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={code},m15,,{lmt}", ["m15"]),
            KlinePeriod.M30 => ($"https://web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={code},m30,,{lmt}", ["m30"]),
            KlinePeriod.M60 => ($"https://web.ifzq.gtimg.cn/appstock/app/kline/mkline?param={code},m60,,{lmt}", ["m60"]),
            _ => ("", Array.Empty<string>()),
        };
        if (url.Length == 0) return [];
        var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(text);
        var bars = new List<KlineBar>();
        if (!doc.RootElement.TryGetProperty("data", out var root) || root.ValueKind != JsonValueKind.Object)
            return bars;
        if (!root.TryGetProperty(code, out var node) && !root.TryGetProperty(code.ToLowerInvariant(), out node))
            return bars;
        JsonElement arr = default;
        var found = false;
        foreach (var key in keys)
        {
            if (node.TryGetProperty(key, out arr) && arr.ValueKind == JsonValueKind.Array)
            {
                found = true;
                break;
            }
        }
        if (!found) return bars;
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Array)
            {
                var parts = item.EnumerateArray().Select(x => x.ToString()).ToArray();
                var bar = ParseBar(parts, 0, 1, 2, 3, 4, 5);
                if (bar is { } b) bars.Add(b);
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                var bar = ParseBar(item.GetString()!.Split(','), 0, 1, 2, 3, 4, 5);
                if (bar is { } b) bars.Add(b);
            }
        }
        return bars;
    }

    static KlineBar? ParseBar(string[] parts, int dateIdx, int open, int close, int high, int low, int vol)
    {
        if (parts.Length <= Math.Max(Math.Max(open, close), Math.Max(high, low))) return null;
        var o = F(parts[open]); var c = F(parts[close]);
        var h = F(parts[high]); var l = F(parts[low]);
        if (o is null || c is null || h is null || l is null) return null;
        if (h <= 0 || l <= 0) return null;
        var date = parts[dateIdx];
        if (date.Length >= 12 && date.Contains(':') == false && date.All(char.IsDigit))
            date = $"{date[..4]}-{date[4..6]}-{date[6..8]} {date[8..10]}:{date[10..12]}";
        return new KlineBar(date, o.Value, c.Value, h.Value, l.Value,
            parts.Length > vol ? F(parts[vol]) : null,
            parts.Length > vol + 1 ? F(parts[vol + 1]) : null);
    }

    static string EmSecId(string code)
    {
        if (GoldMap.TryGetValue(code, out var meta)) return meta.SecId;
        if (code.StartsWith("sz", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("bj", StringComparison.OrdinalIgnoreCase))
            return "0." + code[2..];
        if (code.StartsWith("sh", StringComparison.OrdinalIgnoreCase))
            return "1." + code[2..];
        return code;
    }

    static double? InferAvg(double price, double? vol, double? amt, double? given)
    {
        static bool Near(double a, double b) => b > 0 && Math.Abs(a - b) / b <= 0.25;
        if (given is > 0 && Near(given.Value, price)) return given;
        if (vol is not > 0 || amt is null) return given is > 0 ? given : null;
        var perShare = amt.Value / vol.Value;
        var perLot = amt.Value / (vol.Value * 100.0);
        var pick = Math.Abs(perShare - price) <= Math.Abs(perLot - price) ? perShare : perLot;
        return pick > 0 && Near(pick, price) ? pick : null;
    }

    public static async Task<byte[]?> FetchMinuteImageAsync(string code, CancellationToken ct = default)
    {
        if (IsGold(code)) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(MinuteImgUrl, code));
        req.Headers.Referrer = new Uri("https://finance.sina.com.cn");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public static List<(string Code, string Name)> SearchStocks(string query, int tries = 2)
        => SearchStocksAsync(query, tries).ConfigureAwait(false).GetAwaiter().GetResult();

    public static async Task<List<(string Code, string Name)>> SearchStocksAsync(string query, int tries = 2, CancellationToken ct = default)
    {
        var q = query.Trim();
        if (q.Length == 0) return [];
        var results = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hit in GoldHits(q))
        {
            if (seen.Add(hit.Code))
                results.Add(hit);
        }

        var url = string.Format(SearchUrl, Uri.EscapeDataString(q));
        byte[]? raw = null;
        for (var i = 0; i < tries; i++)
        {
            try
            {
                raw = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
                break;
            }
            catch
            {
                if (i == tries - 1) break;
                await Task.Delay(400, ct).ConfigureAwait(false);
            }
        }
        if (raw is not null)
        {
            var latin1 = Encoding.Latin1.GetString(raw);
            var text = DecodeUnicodeEscapes(latin1);
            foreach (Match m in HintRe.Matches(text))
            {
                foreach (var item in m.Groups[1].Value.Split('^'))
                {
                    var parts = item.Split('~');
                    if (parts.Length >= 3
                        && parts[0] is "sh" or "sz" or "bj"
                        && Regex.IsMatch(parts[1], @"^\d{6}$")
                        && !string.IsNullOrEmpty(parts[2]))
                    {
                        var code = parts[0] + parts[1];
                        if (seen.Add(code))
                            results.Add((code, parts[2]));
                    }
                }
            }
        }
        return results;
    }

    static IEnumerable<(string Code, string Name)> GoldHits(string q)
    {
        var s = q.Trim().ToLowerInvariant();
        if (s is "xau" or "hf_xau" or "xauusd" or "伦敦金" or "伦敦金现货" or "国际金" or "国际金价" or "现货黄金" or "黄金美元")
        {
            yield return ("xau", "伦敦金");
            yield return ("gc", "COMEX金");
            yield break;
        }
        if (s is "gc" or "hf_gc" or "comex" or "comex金" or "纽约金")
        {
            yield return ("gc", "COMEX金");
            yield return ("xau", "伦敦金");
            yield break;
        }
        if (s.Contains("黄金") || s.Contains("金价") || s.Contains("london") || s.Contains("gold"))
        {
            yield return ("xau", "伦敦金");
            yield return ("gc", "COMEX金");
        }
    }

    static async Task<string> HttpGetAsync(string url, CancellationToken ct)
    {
        var bytes = await Http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
        return Encoding.GetEncoding("GBK").GetString(bytes);
    }

    static async Task<string> HttpGetUtf8Async(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://quote.eastmoney.com/");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    static double? Scaled(JsonElement data, string name, double scale)
    {
        if (!data.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n))
            return n / scale;
        return F(el.ToString()) is { } v ? v / scale : null;
    }

    static double? F(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    static double? Num(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n)) return n;
        return F(el.ToString());
    }

    static string DecodeUnicodeEscapes(string s)
        => Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m =>
            ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
}

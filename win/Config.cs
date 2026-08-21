using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FloatQuote;

public sealed class WatchCategory
{
    public string Name { get; set; } = "";
    public bool Visible { get; set; } = true;
    public List<string> Stocks { get; set; } = [];
}

public sealed class AppConfig
{
    public List<string> Stocks { get; set; } = ["sh600519", "sz000001", "sz300750"];
    public List<WatchCategory> Categories { get; set; } = [];
    public int RefreshSeconds { get; set; } = 3;
    public int ChartRefreshSeconds { get; set; } = 30;
    public int AutoSwitchSeconds { get; set; } = 10;
    public string SwitchEffect { get; set; } = "fade";
    public string ChartKind { get; set; } = "minute";
    public string KlinePeriod { get; set; } = "day";
    public int KlineCount { get; set; } = 60;
    public int DisplayCount { get; set; } = 1;
    public int[] Pos { get; set; } = [200, 120];
    public int[] SizeCollapsed { get; set; } = [167, 19];
    public int[] SizeExpanded { get; set; } = [460, 400];
    public bool AlwaysOnTop { get; set; } = true;
}

public static class ConfigStore
{
    static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const int MaxDisplayCount = 99;
    public const int MinKlineCount = 10;
    public const int MaxKlineCount = 200;
    public const string CatAShare = "A股";
    public const string CatGold = "黄金";

    public static readonly Regex CodeRe = new(@"^(?:(?:sh|sz|bj)\d{6}|xau|gc|hf_xau|hf_gc)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CanonicalCode(string code)
        => code.Trim().ToLowerInvariant() switch
        {
            "hf_xau" or "xauusd" => "xau",
            "hf_gc" => "gc",
            var x => x,
        };

    public static string? OverrideDir { get; set; }

    public static string AppDir
    {
        get
        {
            if (!string.IsNullOrEmpty(OverrideDir))
                return OverrideDir;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "FloatQuote.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            var data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FloatQuote");
            Directory.CreateDirectory(data);
            return data;
        }
    }

    public static string ConfigPath => Path.Combine(AppDir, "config.json");

    public static List<string> NormalizeCodes(string text)
    {
        var codes = new List<string>();
        foreach (var tok in Regex.Split(text.Trim().ToLowerInvariant(), @"[\s,，、;；]+"))
        {
            if (CodeRe.IsMatch(tok))
                codes.Add(CanonicalCode(tok));
        }
        return codes;
    }

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        try
        {
            if (File.Exists(ConfigPath))
            {
                var data = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (data != null)
                    cfg = data;
            }
        }
        catch (Exception)
        {
            // keep defaults
        }

        cfg.Categories ??= [];
        cfg.Stocks ??= [];
        var hadCategories = cfg.Categories.Any(c => c.Stocks is { Count: > 0 });
        cfg.Stocks = cfg.Stocks
            .Where(s => CodeRe.IsMatch(s))
            .Select(CanonicalCode)
            .Distinct()
            .ToList();
        EnsureCategories(cfg);
        if (cfg.Stocks.Count == 0)
            cfg.Stocks = ["sh600519", "sz000001", "sz300750"];
        if (cfg.SwitchEffect is not ("off" or "fade" or "slide_h" or "slide_v" or "pulse"))
            cfg.SwitchEffect = "fade";
        cfg.DisplayCount = Math.Clamp(cfg.DisplayCount, 1, MaxDisplayCount);
        var chartKind = cfg.ChartKind?.Trim().ToLowerInvariant();
        if (chartKind is "day" or "week" or "month" or "m1" or "m5" or "m15" or "m30" or "m60")
        {
            cfg.KlinePeriod = chartKind;
            chartKind = "kline";
        }
        cfg.ChartKind = chartKind == "kline" ? "kline" : "minute";
        cfg.KlinePeriod = KlinePeriods.Key(KlinePeriods.Parse(cfg.KlinePeriod));
        cfg.KlineCount = Math.Clamp(cfg.KlineCount <= 0 ? 60 : cfg.KlineCount, MinKlineCount, MaxKlineCount);
        cfg.RefreshSeconds = Math.Max(1, cfg.RefreshSeconds);
        cfg.ChartRefreshSeconds = Math.Max(5, cfg.ChartRefreshSeconds);
        if (!File.Exists(ConfigPath) || !hadCategories)
            Save(cfg);
        return cfg;
    }

    public static string DefaultCategory(string code)
        => QuoteClient.IsGold(code) ? CatGold : CatAShare;

    public static List<WatchCategory> CloneCategories(IEnumerable<WatchCategory> src)
        => src.Select(c => new WatchCategory
        {
            Name = c.Name,
            Visible = c.Visible,
            Stocks = [.. c.Stocks],
        }).ToList();

    public static List<string> AllCodes(AppConfig cfg)
        => EnsureCategories(cfg).SelectMany(c => c.Stocks).ToList();

    public static List<string> VisibleCodes(AppConfig cfg)
    {
        EnsureCategories(cfg);
        var list = cfg.Categories.Where(c => c.Visible).SelectMany(c => c.Stocks).ToList();
        if (list.Count == 0)
        {
            var first = cfg.Categories.FirstOrDefault(c => c.Stocks.Count > 0);
            if (first is not null)
            {
                first.Visible = true;
                list = [.. first.Stocks];
            }
        }
        return list.Count > 0 ? list : cfg.Stocks;
    }

    public static List<WatchCategory> EnsureCategories(AppConfig cfg)
    {
        cfg.Categories ??= [];
        foreach (var cat in cfg.Categories)
        {
            cat.Name = string.IsNullOrWhiteSpace(cat.Name) ? CatAShare : cat.Name.Trim();
            cat.Stocks = (cat.Stocks ?? [])
                .Where(s => CodeRe.IsMatch(s))
                .Select(CanonicalCode)
                .ToList();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in cfg.Categories)
        {
            var keep = new List<string>();
            foreach (var code in cat.Stocks)
            {
                if (seen.Add(code))
                    keep.Add(code);
            }
            cat.Stocks = keep;
        }

        var fromCats = cfg.Categories.SelectMany(c => c.Stocks).ToList();
        if (fromCats.Count == 0)
        {
            var buckets = new List<WatchCategory>();
            WatchCategory Bucket(string name)
            {
                var c = buckets.FirstOrDefault(x => x.Name == name);
                if (c is not null) return c;
                c = new WatchCategory { Name = name, Visible = true };
                buckets.Add(c);
                return c;
            }
            foreach (var code in cfg.Stocks)
                Bucket(DefaultCategory(code)).Stocks.Add(code);
            if (buckets.Count == 0)
                buckets.Add(new WatchCategory { Name = CatAShare, Visible = true, Stocks = ["sh600519", "sz000001", "sz300750"] });
            cfg.Categories = buckets;
            fromCats = cfg.Categories.SelectMany(c => c.Stocks).ToList();
        }
        else
        {
            foreach (var code in cfg.Stocks)
            {
                if (seen.Contains(code)) continue;
                var cat = cfg.Categories.FirstOrDefault(c => c.Name == DefaultCategory(code));
                if (cat is null)
                {
                    cat = new WatchCategory { Name = DefaultCategory(code), Visible = true };
                    cfg.Categories.Add(cat);
                }
                cat.Stocks.Add(code);
                seen.Add(code);
                fromCats.Add(code);
            }
            cfg.Categories = cfg.Categories.Where(c => c.Stocks.Count > 0 || cfg.Categories.Count == 1).ToList();
            if (cfg.Categories.Count == 0)
                cfg.Categories.Add(new WatchCategory { Name = CatAShare, Visible = true });
        }

        if (!cfg.Categories.Any(c => c.Visible && c.Stocks.Count > 0))
        {
            var first = cfg.Categories.FirstOrDefault(c => c.Stocks.Count > 0) ?? cfg.Categories[0];
            first.Visible = true;
        }

        cfg.Stocks = fromCats.Count > 0 ? fromCats : cfg.Stocks;
        return cfg.Categories;
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch (IOException)
        {
        }
    }
}

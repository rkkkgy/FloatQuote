using System.Windows.Media;

namespace FloatQuote;

public static class Theme
{
    public static readonly Color BgMain = Color.FromArgb(245, 30, 34, 45);
    public static readonly Color Border = Color.FromRgb(72, 80, 98);
    public static readonly Color TextMain = Color.FromRgb(232, 235, 242);
    public static readonly Color TextSub = Color.FromRgb(150, 157, 170);
    public static readonly Color Red = Color.FromRgb(228, 77, 67);
    public static readonly Color Green = Color.FromRgb(40, 178, 110);
    public static readonly Color Gray = Color.FromRgb(150, 157, 170);
    public static readonly Color Yellow = Color.FromRgb(245, 194, 66);
    public static readonly Color HoverBg = Color.FromArgb(210, 46, 52, 68);
    public static readonly Color HighlightBg = Color.FromArgb(220, 56, 104, 80);
    public static readonly Color DialogBg = Color.FromRgb(35, 39, 51);
    public static readonly Color FieldBg = Color.FromRgb(27, 31, 42);

    public const string UiFont = "Microsoft YaHei UI";
    public const string MonoFont = "Consolas";

    public static Brush BrushOf(string colorKey) => colorKey switch
    {
        "red" => new SolidColorBrush(Red),
        "green" => new SolidColorBrush(Green),
        _ => new SolidColorBrush(Gray),
    };

    public static Color ColorOf(string colorKey) => colorKey switch
    {
        "red" => Red,
        "green" => Green,
        _ => Gray,
    };
}

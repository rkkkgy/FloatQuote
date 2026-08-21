using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;

namespace FloatQuote;

public static class TrayIconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Make(string colorKey)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(DrawingColor.Transparent);
            using var path = RoundedRect(0, 0, 15, 15, 3);
            using var bg = new SolidBrush(DrawingColor.FromArgb(245, 30, 34, 45));
            using var border = new DrawingPen(DrawingColor.FromArgb(72, 80, 98), 1);
            g.FillPath(bg, path);
            g.DrawPath(border, path);

            var color = colorKey switch
            {
                "red" => DrawingColor.FromArgb(228, 77, 67),
                "green" => DrawingColor.FromArgb(40, 178, 110),
                _ => DrawingColor.FromArgb(150, 157, 170),
            };
            using var pen = new DrawingPen(color, 1.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };
            PointF[] pts = colorKey switch
            {
                "red" => [new(2, 11), new(5, 9), new(8, 10), new(11, 5), new(14, 4)],
                "green" => [new(2, 4), new(5, 6), new(8, 5), new(11, 11), new(14, 12)],
                _ => [new(2, 8), new(5, 7), new(8, 9), new(11, 8), new(14, 8)],
            };
            g.DrawLines(pen, pts);
        }

        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    static GraphicsPath RoundedRect(int x, int y, int w, int h, int r)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, r * 2);
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

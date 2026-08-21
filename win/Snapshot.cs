using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DrawingSize = System.Drawing.Size;

namespace FloatQuote;

public static class Snapshot
{
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    public static void Save(Window win, string path)
    {
        win.UpdateLayout();
        DispatcherPump();
        var hwnd = new WindowInteropHelper(win).EnsureHandle();
        if (!GetWindowRect(hwnd, out var r))
            throw new InvalidOperationException("GetWindowRect failed");
        var w = Math.Max(1, r.Right - r.Left);
        var h = Math.Max(1, r.Bottom - r.Top);
        using var bmp = new Bitmap(w, h);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, new DrawingSize(w, h));
        bmp.Save(path, ImageFormat.Png);
    }

    static void DispatcherPump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Render,
            () => frame.Continue = false);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
}

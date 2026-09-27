using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace TurboToggle;
static class IconFactory
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);
    static Icon? _onIcon;
    static Icon? _offIcon;
    static readonly object _iconLock = new();
    public static Icon Get(bool on)
    {
        lock (_iconLock)
        {
            if (on)
                return _onIcon ??= Create(on: true);
            return _offIcon ??= Create(on: false);
        }
    }
    static Icon Create(bool on)
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var color = on ? Color.FromArgb(76, 175, 80) : Color.FromArgb(140, 140, 140);
            using (var path = RoundedRect(new Rectangle(4, 4, 56, 56), 12))
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
            using (var brush = new SolidBrush(Color.White))
                g.FillPolygon(brush, new[]
                {
                    new PointF(36, 10), new PointF(16, 36), new PointF(28, 36),
                    new PointF(24, 54), new PointF(46, 28), new PointF(33, 28),
                    new PointF(40, 10),
                });
        }
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(hIcon);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
    public static void Release()
    {
        lock (_iconLock)
        {
            _onIcon?.Dispose();
            _onIcon = null;
            _offIcon?.Dispose();
            _offIcon = null;
        }
    }
    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        int d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

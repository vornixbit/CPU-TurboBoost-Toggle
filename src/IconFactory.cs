using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;

namespace TurboToggle;

static class IconFactory
{
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    static extern bool DestroyIcon(IntPtr handle);

    static readonly Lazy<Icon> OnIcon = new(() => Create(on: true), LazyThreadSafetyMode.ExecutionAndPublication);
    static readonly Lazy<Icon> OffIcon = new(() => Create(on: false), LazyThreadSafetyMode.ExecutionAndPublication);

    public static Icon Get(bool on) => (on ? OnIcon : OffIcon).Value;

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

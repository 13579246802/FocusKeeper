using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FocusKeeper;

/// <summary>
/// 生成程序图标（多尺寸 ICO，内嵌 PNG 帧）。
/// 只为开发期用一次：`FocusKeeper.exe --makeicon 路径`。界面风格与主窗口一致（深底 + 绿环 + 缺口钟面）。
/// </summary>
internal static class IconMaker
{
    private static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    public static void Write(string path)
    {
        var frames = Sizes.Select(Render).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);

        // ICONDIR
        w.Write((ushort)0);
        w.Write((ushort)1);          // 1 = icon
        w.Write((ushort)frames.Count);

        var offset = 6 + 16 * frames.Count;
        for (var i = 0; i < frames.Count; i++)
        {
            var s = Sizes[i];
            w.Write((byte)(s >= 256 ? 0 : s));   // 0 表示 256
            w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0);                     // 调色板数
            w.Write((byte)0);                     // 保留
            w.Write((ushort)1);                   // 色彩平面
            w.Write((ushort)32);                  // 位深
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var f in frames) w.Write(f);
    }

    private static byte[] Render(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var pad = size * 0.03f;
            var rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            using (var bg = RoundedRect(rect, size * 0.22f))
            using (var brush = new SolidBrush(Color.FromArgb(18, 20, 23)))
                g.FillPath(brush, bg);

            // 3/4 圈，缺口留在右上——像一只正在走的表，不像「已停止」的圆
            var ring = new RectangleF(size * 0.245f, size * 0.245f, size * 0.51f, size * 0.51f);
            var penW = Math.Max(1.1f, size * 0.082f);
            using (var pen = new Pen(Color.FromArgb(126, 214, 160), penW)
                   { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(pen, ring, -90, 270);

            using (var dot = new SolidBrush(Color.FromArgb(126, 214, 160)))
            {
                var d = size * 0.095f;
                g.FillEllipse(dot, size / 2f - d / 2, size / 2f - d / 2, d, d);
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

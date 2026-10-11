using System.Drawing.Drawing2D;

namespace GeckoPeeks.Ui;

/// <summary>
/// A small original gecko: cream body, amber eye, curled tail, jungle disc.
/// It is not the Mozilla mark or the Geico mark.
/// </summary>
internal static class GeckoMark
{
    public static Bitmap CreateBitmap(int size)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        var scale = size / 32f;

        using var background = new SolidBrush(Color.FromArgb(10, 52, 38));
        graphics.FillEllipse(background, scale, scale, 30 * scale, 30 * scale);

        using var body = new SolidBrush(Color.FromArgb(236, 226, 206));
        using var amber = new SolidBrush(Theme.Amber);
        using var ink = new Pen(Theme.Amber, Math.Max(1.5f, 2f * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        graphics.DrawBezier(
            ink,
            9 * scale, 22 * scale,
            2 * scale, 28 * scale,
            3 * scale, 12 * scale,
            12 * scale, 16 * scale);
        graphics.FillEllipse(body, 10 * scale, 14 * scale, 14 * scale, 9 * scale);
        graphics.FillEllipse(body, 16 * scale, 8 * scale, 10 * scale, 9 * scale);
        graphics.FillEllipse(amber, 21.2f * scale, 10.6f * scale, 3.1f * scale, 3.1f * scale);
        graphics.DrawLine(ink, 14 * scale, 22 * scale, 12 * scale, 26 * scale);
        graphics.DrawLine(ink, 20 * scale, 22 * scale, 22 * scale, 26 * scale);
        return bitmap;
    }

    public static Icon CreateIcon()
    {
        using var bitmap = CreateBitmap(32);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}

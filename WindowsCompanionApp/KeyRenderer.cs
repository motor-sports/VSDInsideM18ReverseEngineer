using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace VSDM18App;

/// <summary>
/// Turns a key's image / label / background into the 64x64 JPEG the device shows.
/// The editor preview uses exactly the same bytes, so what you see is what the deck shows.
/// </summary>
public static class KeyRenderer
{
    private const int Size = 64;
    private static readonly Dictionary<string, byte[]> Cache = new();
    private static readonly object CacheLock = new();

    public static bool IsBlank(KeyConfig k) =>
        k.Display == KeyDisplay.None &&
        string.IsNullOrEmpty(k.ImageFile) &&
        string.IsNullOrWhiteSpace(k.Label) &&
        Rgb.Parse(k.Background, Rgb.Black) == Rgb.Black;

    /// <param name="liveText">Value to draw large in the middle (gear, RPM...). Null = none.</param>
    /// <param name="backgroundOverride">Replaces the key's background colour (used for RPM colouring and flashing).</param>
    public static byte[] RenderCached(KeyConfig k, string? liveText = null, Rgb? backgroundOverride = null)
    {
        string id = $"{k.ImageFile}|{k.Label}|{k.Background}|{liveText}|{backgroundOverride?.ToHex()}";
        lock (CacheLock)
        {
            if (Cache.TryGetValue(id, out var hit)) return hit;
        }

        byte[] bytes = Render(k, liveText, backgroundOverride);
        lock (CacheLock)
        {
            if (Cache.Count > 400) Cache.Clear();
            Cache[id] = bytes;
        }
        return bytes;
    }

    private static byte[] Render(KeyConfig k, string? liveText, Rgb? backgroundOverride)
    {
        var bg = backgroundOverride ?? Rgb.Parse(k.Background, Rgb.Black);

        using var bmp = new Bitmap(Size, Size, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(bg.R, bg.G, bg.B));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            bool hasImage = false;
            if (!string.IsNullOrEmpty(k.ImageFile))
            {
                string path = Path.Combine(ConfigStore.ImagesDir, k.ImageFile);
                if (File.Exists(path))
                {
                    try
                    {
                        using var fs = File.OpenRead(path);
                        using var img = Image.FromStream(fs);
                        DrawCover(g, img);
                        hasImage = true;
                    }
                    catch { /* unreadable image: fall back to label / colour only */ }
                }
            }

            bool hasLive = !string.IsNullOrEmpty(liveText);
            if (hasLive) DrawBigText(g, liveText!);

            if (!string.IsNullOrWhiteSpace(k.Label))
                DrawLabel(g, k.Label.Trim(), hasImage || hasLive, hasLive);
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    // Fill the key and crop the overflow, instead of squashing the picture.
    private static void DrawCover(Graphics g, Image img)
    {
        float scale = Math.Max((float)Size / img.Width, (float)Size / img.Height);
        float w = img.Width * scale, h = img.Height * scale;
        var dest = new Rectangle(
            (int)Math.Round((Size - w) / 2f), (int)Math.Round((Size - h) / 2f),
            (int)Math.Ceiling(w), (int)Math.Ceiling(h));

        using var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY); // avoids faded edges
        g.DrawImage(img, dest, 0f, 0f, (float)img.Width, (float)img.Height, GraphicsUnit.Pixel, attrs);
    }

    // Big centred value, e.g. the gear or RPM.
    private static void DrawBigText(Graphics g, string text)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float em = text.Length <= 1 ? 44f : text.Length == 2 ? 36f : text.Length == 3 ? 28f : text.Length == 4 ? 23f : 19f;
        var rect = new RectangleF(0, -4, Size, Size);

        using var family = new FontFamily("Segoe UI");
        using var sf = new StringFormat(StringFormatFlags.NoClip | StringFormatFlags.NoWrap)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, em, rect, sf);

        using var pen = new Pen(Color.FromArgb(220, 0, 0, 0), 4f) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.FillPath(Brushes.White, path);
    }

    private static void DrawLabel(Graphics g, string text, bool atBottom, bool small)
    {
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        float em = small ? 9f : text.Length <= 6 ? 14f : text.Length <= 12 ? 11f : 9f;
        var rect = new RectangleF(2, 2, Size - 4, Size - 4);

        using var family = new FontFamily("Segoe UI");
        using var sf = new StringFormat(StringFormatFlags.NoClip)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = atBottom ? StringAlignment.Far : StringAlignment.Center,
            Trimming = StringTrimming.EllipsisWord
        };
        using var path = new GraphicsPath();
        path.AddString(text, family, (int)FontStyle.Bold, em, rect, sf);

        using var pen = new Pen(Color.FromArgb(220, 0, 0, 0), 3f) { LineJoin = LineJoin.Round };
        g.DrawPath(pen, path);
        g.FillPath(Brushes.White, path);
    }

    public static System.Windows.Media.Imaging.BitmapSource ToBitmapSource(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        var bi = new System.Windows.Media.Imaging.BitmapImage();
        bi.BeginInit();
        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }
}

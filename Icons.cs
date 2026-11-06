using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LiteMixer;

/// <summary>App icons for chips (cached per exe path) and the tray icon variants.</summary>
internal static class Icons
{
    static readonly Dictionary<string, ImageSource?> AppIcons = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? ForExe(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (AppIcons.TryGetValue(path, out var cached)) return cached;

        ImageSource? image = null;
        var small = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(small[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                image = source;
            }
        }
        catch { }
        finally
        {
            if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
        }

        AppIcons[path] = image;
        return image;
    }

    /// <summary>The app icon with a red "mic muted" badge, as an HICON for the tray. Caller destroys it.</summary>
    public static IntPtr CreateMutedTrayIcon()
    {
        int size = GetSystemMetrics(49 /* SM_CXSMICON */);
        if (size <= 0) size = 16;
        try
        {
            var decoder = new IconBitmapDecoder(new Uri("pack://application:,,,/app.ico"),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(frame, new Rect(0, 0, size, size));
                double r = size * 0.3;
                var center = new Point(size - r, size - r);
                dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF0, 0x40, 0x40)), null, center, r, r);
                var pen = new Pen(Brushes.White, Math.Max(1.5, size / 10.0)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, new Point(center.X - r * 0.5, center.Y), new Point(center.X + r * 0.5, center.Y));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            var png = stream.ToArray();
            return CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, size, size, 0);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);

    [DllImport("user32.dll")]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, bool icon, uint version, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);
}

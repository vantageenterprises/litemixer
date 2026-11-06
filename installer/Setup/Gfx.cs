using System.Runtime.InteropServices;

namespace LiteMixerSetup;

/// <summary>Immediate-mode drawing over the GDI+ flat API, in logical pixels.</summary>
internal sealed unsafe partial class Gfx : IDisposable
{
    public enum Align { Left = 0, Center = 1, Right = 2 }

    static nuint _token;
    static readonly Dictionary<(string, float, int), nint> Fonts = new();
    static readonly Dictionary<string, nint> Families = new();
    static nint _formatLeft, _formatCenter, _formatRight;

    readonly nint _g;

    public static void Startup()
    {
        var input = new StartupInput { Version = 1 };
        GdiplusStartup(out _token, ref input, 0);
        GdipStringFormatGetGenericTypographic(out var typographic);
        GdipCloneStringFormat(typographic, out _formatLeft);
        GdipCloneStringFormat(typographic, out _formatCenter);
        GdipCloneStringFormat(typographic, out _formatRight);
        GdipSetStringFormatAlign(_formatCenter, 1);
        GdipSetStringFormatAlign(_formatRight, 2);
    }

    public Gfx(nint graphics, float scale)
    {
        _g = graphics;
        GdipSetSmoothingMode(_g, 4 /* AntiAlias */);
        GdipSetPixelOffsetMode(_g, 4 /* Half */);
        GdipSetTextRenderingHint(_g, 5 /* ClearTypeGridFit */);
        GdipScaleWorldTransform(_g, scale, scale, 0);
    }

    public static Gfx FromHdc(nint hdc, float scale)
    {
        GdipCreateFromHDC(hdc, out var graphics);
        return new Gfx(graphics, scale);
    }

    public void Dispose() => GdipDeleteGraphics(_g);

    public void Clear(uint color) => GdipGraphicsClear(_g, color);

    public void Rect(float x, float y, float w, float h, uint color)
    {
        GdipCreateSolidFill(color, out var brush);
        GdipFillRectangle(_g, brush, x, y, w, h);
        GdipDeleteBrush(brush);
    }

    public void RoundRect(float x, float y, float w, float h, float r, uint color)
    {
        GdipCreateSolidFill(color, out var brush);
        FillRounded(x, y, w, h, r, brush);
        GdipDeleteBrush(brush);
    }

    /// <summary>Rounded rectangle with a linear gradient from (x1,y1) to (x2,y2).</summary>
    public void RoundRectGradient(float x, float y, float w, float h, float r, uint from, uint to, bool vertical)
    {
        var start = new PointF { X = x, Y = y };
        var end = vertical ? new PointF { X = x, Y = y + Math.Max(h, 1) } : new PointF { X = x + Math.Max(w, 1), Y = y };
        GdipCreateLineBrush(ref start, ref end, from, to, 0, out var brush);
        FillRounded(x, y, w, h, r, brush);
        GdipDeleteBrush(brush);
    }

    void FillRounded(float x, float y, float w, float h, float r, nint brush)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        if (r <= 0.01f)
        {
            GdipFillRectangle(_g, brush, x, y, w, h);
            return;
        }
        float d = r * 2;
        GdipCreatePath(0, out var path);
        GdipAddPathArc(path, x, y, d, d, 180, 90);
        GdipAddPathArc(path, x + w - d, y, d, d, 270, 90);
        GdipAddPathArc(path, x + w - d, y + h - d, d, d, 0, 90);
        GdipAddPathArc(path, x, y + h - d, d, d, 90, 90);
        GdipClosePathFigure(path);
        GdipFillPath(_g, brush, path);
        GdipDeletePath(path);
    }

    public void Ellipse(float cx, float cy, float rx, float ry, uint color)
    {
        GdipCreateSolidFill(color, out var brush);
        GdipFillEllipse(_g, brush, cx - rx, cy - ry, rx * 2, ry * 2);
        GdipDeleteBrush(brush);
    }

    /// <summary>Soft radial light: the color at the center fading to nothing at the edge.</summary>
    public void Glow(float cx, float cy, float rx, float ry, uint color)
    {
        GdipCreatePath(0, out var path);
        GdipAddPathEllipse(path, cx - rx, cy - ry, rx * 2, ry * 2);
        GdipCreatePathGradientFromPath(path, out var brush);
        GdipSetPathGradientCenterColor(brush, color);
        uint edge = color & 0x00FFFFFF;
        int count = 1;
        GdipSetPathGradientSurroundColorsWithCount(brush, &edge, ref count);
        GdipFillPath(_g, brush, path);
        GdipDeleteBrush(brush);
        GdipDeletePath(path);
    }

    public void Line(float x1, float y1, float x2, float y2, float width, uint color)
    {
        GdipCreatePen1(color, width, 0 /* UnitWorld */, out var pen);
        GdipSetPenStartCap(pen, 2 /* Round */);
        GdipSetPenEndCap(pen, 2);
        GdipDrawLine(_g, pen, x1, y1, x2, y2);
        GdipDeletePen(pen);
    }

    public void Text(string text, float x, float y, float w, float h, string family, float size, uint color,
        Align align = Align.Left, bool bold = false)
    {
        var font = GetFont(family, size, bold);
        if (font == 0) return;
        GdipCreateSolidFill(color, out var brush);
        var format = align switch { Align.Center => _formatCenter, Align.Right => _formatRight, _ => _formatLeft };
        var rect = new RectF { X = x, Y = y, W = w, H = h };
        fixed (char* chars = text)
            GdipDrawString(_g, chars, text.Length, font, ref rect, format, brush);
        GdipDeleteBrush(brush);
    }

    static nint GetFont(string family, float size, bool bold)
    {
        var key = (family, size, bold ? 1 : 0);
        if (Fonts.TryGetValue(key, out var font)) return font;
        if (!Families.TryGetValue(family, out var handle))
        {
            if (GdipCreateFontFamilyFromName(family, 0, out handle) != 0)
                GdipCreateFontFamilyFromName("Segoe UI", 0, out handle);
            Families[family] = handle;
        }
        GdipCreateFont(handle, size, bold ? 1 : 0, 2 /* UnitPixel */, out font);
        Fonts[key] = font;
        return font;
    }

    /// <summary>Scales a color's alpha, for fades.</summary>
    public static uint Fade(uint color, double opacity)
    {
        uint alpha = (uint)Math.Clamp((color >> 24) * opacity, 0, 255);
        return (alpha << 24) | (color & 0x00FFFFFF);
    }

    /// <summary>Renders into an offscreen bitmap and saves it as a PNG (used for build-time visual checks).</summary>
    public static void RenderToPng(int width, int height, float scale, Action<Gfx> draw, string path)
    {
        GdipCreateBitmapFromScan0(width, height, 0, 0x26200A /* 32bpp ARGB */, 0, out var bitmap);
        GdipGetImageGraphicsContext(bitmap, out var graphics);
        using (var gfx = new Gfx(graphics, scale)) draw(gfx);
        var png = new Guid("557CF406-1A04-11D3-9A73-0000F81EF32E");
        GdipSaveImageToFile(bitmap, path, ref png, 0);
        GdipDisposeImage(bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct StartupInput { public uint Version; public nint Callback; public int NoThread; public int NoCodecs; }
    [StructLayout(LayoutKind.Sequential)] struct PointF { public float X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RectF { public float X, Y, W, H; }

    const string Gdip = "gdiplus.dll";
    [LibraryImport(Gdip)] private static partial int GdiplusStartup(out nuint token, ref StartupInput input, nint output);
    [LibraryImport(Gdip)] private static partial int GdipCreateFromHDC(nint hdc, out nint graphics);
    [LibraryImport(Gdip)] private static partial int GdipDeleteGraphics(nint graphics);
    [LibraryImport(Gdip)] private static partial int GdipSetSmoothingMode(nint graphics, int mode);
    [LibraryImport(Gdip)] private static partial int GdipSetPixelOffsetMode(nint graphics, int mode);
    [LibraryImport(Gdip)] private static partial int GdipSetTextRenderingHint(nint graphics, int hint);
    [LibraryImport(Gdip)] private static partial int GdipScaleWorldTransform(nint graphics, float sx, float sy, int order);
    [LibraryImport(Gdip)] private static partial int GdipGraphicsClear(nint graphics, uint color);
    [LibraryImport(Gdip)] private static partial int GdipCreateSolidFill(uint color, out nint brush);
    [LibraryImport(Gdip)] private static partial int GdipDeleteBrush(nint brush);
    [LibraryImport(Gdip)] private static partial int GdipFillRectangle(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport(Gdip)] private static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float w, float h);
    [LibraryImport(Gdip)] private static partial int GdipCreateLineBrush(ref PointF p1, ref PointF p2, uint c1, uint c2, int wrap, out nint brush);
    [LibraryImport(Gdip)] private static partial int GdipCreatePath(int fillMode, out nint path);
    [LibraryImport(Gdip)] private static partial int GdipDeletePath(nint path);
    [LibraryImport(Gdip)] private static partial int GdipAddPathArc(nint path, float x, float y, float w, float h, float start, float sweep);
    [LibraryImport(Gdip)] private static partial int GdipAddPathEllipse(nint path, float x, float y, float w, float h);
    [LibraryImport(Gdip)] private static partial int GdipClosePathFigure(nint path);
    [LibraryImport(Gdip)] private static partial int GdipFillPath(nint graphics, nint brush, nint path);
    [LibraryImport(Gdip)] private static partial int GdipCreatePathGradientFromPath(nint path, out nint brush);
    [LibraryImport(Gdip)] private static partial int GdipSetPathGradientCenterColor(nint brush, uint color);
    [LibraryImport(Gdip)] private static partial int GdipSetPathGradientSurroundColorsWithCount(nint brush, uint* colors, ref int count);
    [LibraryImport(Gdip)] private static partial int GdipCreatePen1(uint color, float width, int unit, out nint pen);
    [LibraryImport(Gdip)] private static partial int GdipDeletePen(nint pen);
    [LibraryImport(Gdip)] private static partial int GdipSetPenStartCap(nint pen, int cap);
    [LibraryImport(Gdip)] private static partial int GdipSetPenEndCap(nint pen, int cap);
    [LibraryImport(Gdip)] private static partial int GdipDrawLine(nint graphics, nint pen, float x1, float y1, float x2, float y2);
    [LibraryImport(Gdip, StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipCreateFontFamilyFromName(string name, nint collection, out nint family);
    [LibraryImport(Gdip)] private static partial int GdipCreateFont(nint family, float size, int style, int unit, out nint font);
    [LibraryImport(Gdip)] private static partial int GdipStringFormatGetGenericTypographic(out nint format);
    [LibraryImport(Gdip)] private static partial int GdipCloneStringFormat(nint format, out nint clone);
    [LibraryImport(Gdip)] private static partial int GdipSetStringFormatAlign(nint format, int align);
    [LibraryImport(Gdip)] private static partial int GdipDrawString(nint graphics, char* text, int length, nint font, ref RectF rect, nint format, nint brush);
    [LibraryImport(Gdip)] private static partial int GdipCreateBitmapFromScan0(int w, int h, int stride, int format, nint scan0, out nint bitmap);
    [LibraryImport(Gdip)] private static partial int GdipGetImageGraphicsContext(nint image, out nint graphics);
    [LibraryImport(Gdip, StringMarshalling = StringMarshalling.Utf16)] private static partial int GdipSaveImageToFile(nint image, string file, ref Guid encoder, nint parameters);
    [LibraryImport(Gdip)] private static partial int GdipDisposeImage(nint image);
}

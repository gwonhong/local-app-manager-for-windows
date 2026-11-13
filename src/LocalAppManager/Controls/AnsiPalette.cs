using LocalAppManager.Core.Logs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace LocalAppManager.Controls;

/// <summary>
/// Maps ANSI colors to brushes for the current theme. Foreground colors are nudged for contrast:
/// "white" text stays readable on a light background, "black" text on a dark one.
/// </summary>
public static class AnsiPalette
{
    // Foreground palettes (One Half–style), tuned per theme.
    private static readonly uint[] DarkFg =
    [
        0x7F848E, 0xE06C75, 0x98C379, 0xE5C07B, 0x61AFEF, 0xC678DD, 0x56B6C2, 0xDCDFE4,
        0x9DA1AA, 0xFF7B86, 0xB5E890, 0xF0D197, 0x85C5FF, 0xDA9EF0, 0x7DD3DE, 0xFFFFFF,
    ];

    private static readonly uint[] LightFg =
    [
        0x383A42, 0xD03B2E, 0x3E8E3D, 0x9A6A00, 0x0B6FB0, 0xA626A4, 0x0A7C93, 0x6B6F76,
        0x5C5F68, 0xCA1243, 0x2F7A2E, 0x8A5A00, 0x2F63C9, 0x8F2290, 0x086B80, 0x3A3D44,
    ];

    // Background palette (Campbell); backgrounds are drawn as-is.
    private static readonly uint[] Bg =
    [
        0x0C0C0C, 0xC50F1F, 0x13A10E, 0xC19C00, 0x0037DA, 0x881798, 0x3A96DD, 0xCCCCCC,
        0x767676, 0xE74856, 0x16C60C, 0xF9F1A5, 0x3B78FF, 0xB4009E, 0x61D6D6, 0xF2F2F2,
    ];

    private static readonly Dictionary<uint, SolidColorBrush> Cache = new();

    public static SolidColorBrush? Foreground(AnsiColor color, ElementTheme theme, bool dim)
    {
        if (color.IsDefault) return null;
        uint rgb = color.Kind == AnsiColorKind.Palette
            ? color.Index < 16 ? (theme == ElementTheme.Light ? LightFg : DarkFg)[color.Index] : Xterm256(color.Index)
            : Pack(color.R, color.G, color.B);
        rgb = EnsureContrast(rgb, theme);
        return Brush(rgb, dim ? (byte)0xA8 : (byte)0xFF);
    }

    public static SolidColorBrush? Background(AnsiColor color)
    {
        if (color.IsDefault) return null;
        uint rgb = color.Kind == AnsiColorKind.Palette
            ? color.Index < 16 ? Bg[color.Index] : Xterm256(color.Index)
            : Pack(color.R, color.G, color.B);
        return Brush(rgb, 0xFF);
    }

    /// <summary>Text color that reads well on top of <paramref name="background"/>.</summary>
    public static SolidColorBrush ContrastingText(SolidColorBrush background)
    {
        var c = background.Color;
        return Luminance(Pack(c.R, c.G, c.B)) > 0.5 ? Brush(0x101010, 0xFF) : Brush(0xF5F5F5, 0xFF);
    }

    /// <summary>Default text color as an opaque brush (needed when inverse video swaps it into the background).</summary>
    public static SolidColorBrush DefaultText(ElementTheme theme) => theme == ElementTheme.Light ? Brush(0x1B1B1B, 0xFF) : Brush(0xE6E6E6, 0xFF);

    public static SolidColorBrush DefaultBackground(ElementTheme theme) => theme == ElementTheme.Light ? Brush(0xFFFFFF, 0xFF) : Brush(0x1F1F1F, 0xFF);

    private static uint Xterm256(byte index)
    {
        if (index < 16) return Bg[index];
        if (index >= 232)
        {
            int v = 8 + (index - 232) * 10;
            return Pack((byte)v, (byte)v, (byte)v);
        }
        int i = index - 16;
        int[] levels = [0, 95, 135, 175, 215, 255];
        return Pack((byte)levels[i / 36], (byte)levels[i / 6 % 6], (byte)levels[i % 6]);
    }

    private static uint EnsureContrast(uint rgb, ElementTheme theme)
    {
        double l = Luminance(rgb);
        if (theme == ElementTheme.Light && l > 0.72) return Scale(rgb, 0.55);
        if (theme != ElementTheme.Light && l < 0.12) return Mix(rgb, 0x9DA1AA, 0.7);
        return rgb;
    }

    private static double Luminance(uint rgb)
    {
        static double Ch(uint v)
        {
            double c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(rgb >> 16 & 0xFF) + 0.7152 * Ch(rgb >> 8 & 0xFF) + 0.0722 * Ch(rgb & 0xFF);
    }

    private static uint Scale(uint rgb, double f) =>
        Pack((byte)((rgb >> 16 & 0xFF) * f), (byte)((rgb >> 8 & 0xFF) * f), (byte)((rgb & 0xFF) * f));

    private static uint Mix(uint a, uint b, double t)
    {
        byte M(int sa, int sb) => (byte)(sa + (sb - sa) * t);
        return Pack(M((int)(a >> 16 & 0xFF), (int)(b >> 16 & 0xFF)), M((int)(a >> 8 & 0xFF), (int)(b >> 8 & 0xFF)), M((int)(a & 0xFF), (int)(b & 0xFF)));
    }

    private static uint Pack(byte r, byte g, byte b) => (uint)(r << 16 | g << 8 | b);

    private static SolidColorBrush Brush(uint rgb, byte alpha)
    {
        uint key = (uint)alpha << 24 | rgb;
        if (!Cache.TryGetValue(key, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            Cache[key] = brush;
        }
        return brush;
    }
}

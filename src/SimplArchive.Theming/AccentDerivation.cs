using System.Globalization;

namespace SimplArchive.Theming;

/// <summary>
/// Builds a whole accent — hover, tint, selection, the colour of text on a filled button — out of the single
/// colour a customer actually knows about: theirs (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// This exists because the obvious alternative is wrong in a way that is hard to see. If an override merges
/// value-by-value over the shipped palette, a customer who sets only <c>primary</c> gets <em>their</em> buttons
/// and <em>our</em> links, tints and selection — a half-applied brand that looks like a bug and is nobody's
/// design. Deriving instead means the smallest useful custom theme is one line.
/// </para>
/// <para>
/// It also solves dark mode. A brand colour chosen against white is usually far too dark against near-black:
/// a deep aubergine that reads beautifully on a light surface scores 1.7:1 on a dark one. Asking every customer
/// to supply a second, lighter variant would mean most of them supply nothing and get refused, so the dark
/// accent is lifted from the light one until it clears the same bar the validator applies afterwards. Anything
/// stated explicitly always wins — this only fills silence.
/// </para>
/// </remarks>
public static class AccentDerivation
{
    // How far a tint and a selection background sit from the surface they lie on. Low enough that text keeps
    // its own contrast, high enough that the row is visibly picked out.
    private const double TintTowardsSurface = 0.86;
    private const double SelectionTowardsSurface = 0.91;

    /// <summary>A complete accent derived from one colour, guaranteed readable on <paramref name="surface"/>.</summary>
    public static AccentTokens From(string primary, string surface, bool dark)
    {
        // On a dark surface a brand colour usually has to be lifted; on a light one it occasionally has to be
        // deepened. Same loop, opposite direction.
        var readable = MakeReadable(primary, surface, dark);

        var hover = dark ? Lighten(readable, 0.08) : Darken(readable, 0.08);
        var onPrimary = Contrast.Between("#FFFFFF", readable) >= Contrast.Between("#14161C", readable)
            ? "#FFFFFF"
            : "#14161C";

        return new AccentTokens(
            Primary: readable,
            Hover: hover,
            OnPrimary: onPrimary,
            Text: readable,
            Tint: Mix(readable, surface, TintTowardsSurface),
            Selection: Mix(readable, surface, SelectionTowardsSurface));
    }

    /// <summary>Nudges a colour's lightness until it clears WCAG AA against the surface it will sit on.</summary>
    /// <remarks>
    /// Steps rather than solving directly: the relationship between HSL lightness and relative luminance is not
    /// linear, and a step of 2% is smaller than the eye's tolerance for "is that still our colour". The cap
    /// exists so a fully-saturated colour on a mid-grey surface terminates rather than looping; the validator
    /// then refuses whatever came out, which is the correct answer for a hue that genuinely cannot work.
    /// </remarks>
    private static string MakeReadable(string colour, string surface, bool dark)
    {
        var candidate = colour;
        for (var step = 0; step < 40 && Contrast.Between(candidate, surface) < Contrast.MinimumAa; step++)
        {
            candidate = dark ? Lighten(candidate, 0.02) : Darken(candidate, 0.02);
        }

        return candidate;
    }

    /// <summary>The same hue, a step lighter (positive) or darker (negative) in HSL lightness.</summary>
    /// <remarks>
    /// Public because the Avalonia emitter needs it: Fluent derives selection, hover and focus from
    /// <c>SystemAccentColor</c> and its six shades, so those have to be generated from the accent or the
    /// framework keeps painting a selected row in ITS blue while everything around it is the brand's.
    /// </remarks>
    public static string Shade(string hex, double amount) =>
        WithLightness(hex, l => Math.Clamp(l + amount, 0, 1));

    private static string Lighten(string hex, double amount) => Shade(hex, amount);

    private static string Darken(string hex, double amount) => Shade(hex, -amount);

    private static string WithLightness(string hex, Func<double, double> change)
    {
        var (r, g, b) = ToRgb(hex);
        var (h, s, l) = ToHsl(r, g, b);
        return FromHsl(h, s, change(l));
    }

    /// <summary>Linear blend in sRGB. <paramref name="towards"/> 0 is all colour, 1 is all background.</summary>
    private static string Mix(string colour, string background, double towards)
    {
        var (r1, g1, b1) = ToRgb(colour);
        var (r2, g2, b2) = ToRgb(background);
        return Hex(
            r1 + ((r2 - r1) * towards),
            g1 + ((g2 - g1) * towards),
            b1 + ((b2 - b1) * towards));
    }

    private static (double R, double G, double B) ToRgb(string hex)
    {
        var value = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (((value >> 16) & 0xFF) / 255.0, ((value >> 8) & 0xFF) / 255.0, (value & 0xFF) / 255.0);
    }

    private static (double H, double S, double L) ToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        var delta = max - min;

        if (delta < 1e-9)
        {
            return (0, 0, l);
        }

        var s = l > 0.5 ? delta / (2 - max - min) : delta / (max + min);
        var h = max == r ? ((g - b) / delta) + (g < b ? 6 : 0)
            : max == g ? ((b - r) / delta) + 2
            : ((r - g) / delta) + 4;

        return (h / 6, s, l);
    }

    private static string FromHsl(double h, double s, double l)
    {
        if (s < 1e-9)
        {
            return Hex(l, l, l);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;
        return Hex(Component(p, q, h + (1.0 / 3)), Component(p, q, h), Component(p, q, h - (1.0 / 3)));
    }

    private static double Component(double p, double q, double t)
    {
        t = t < 0 ? t + 1 : t > 1 ? t - 1 : t;
        return t < 1.0 / 6 ? p + ((q - p) * 6 * t)
            : t < 1.0 / 2 ? q
            : t < 2.0 / 3 ? p + ((q - p) * ((2.0 / 3) - t) * 6)
            : p;
    }

    private static string Hex(double r, double g, double b) =>
        $"#{Byte(r):X2}{Byte(g):X2}{Byte(b):X2}";

    private static int Byte(double channel) => (int)Math.Round(Math.Clamp(channel, 0, 1) * 255);
}

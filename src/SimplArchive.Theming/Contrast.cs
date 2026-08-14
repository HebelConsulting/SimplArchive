using System.Globalization;

namespace SimplArchive.Theming;

/// <summary>
/// WCAG 2.1 relative luminance and contrast ratio — how a custom theme is checked before it is allowed on
/// screen (ADR 0578).
/// </summary>
/// <remarks>
/// Here because somebody <b>will</b> hand us their corporate pale yellow. Accepting it produces a product that
/// looks broken and reads as our fault; rejecting it with the measured ratio produces one line in a log that
/// says exactly what to change. The shipped palette is held to the same test, so the rule cannot quietly become
/// "everyone except us".
/// </remarks>
public static class Contrast
{
    /// <summary>WCAG AA for normal-size text. The bar a custom theme has to clear.</summary>
    public const double MinimumAa = 4.5;

    /// <summary>Contrast ratio between two <c>#RRGGBB</c> colours: 1.0 (identical) to 21.0 (black on white).</summary>
    public static double Between(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        var (lighter, darker) = a >= b ? (a, b) : (b, a);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>True when <paramref name="hex"/> is a well-formed <c>#RRGGBB</c> colour.</summary>
    public static bool IsColour(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#'
        && int.TryParse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);

    private static double Luminance(string hex)
    {
        var value = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var r = Channel(((value >> 16) & 0xFF) / 255.0);
        var g = Channel(((value >> 8) & 0xFF) / 255.0);
        var b = Channel((value & 0xFF) / 255.0);
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    // The sRGB transfer function. The 0.03928 knee and the 2.4 exponent are the specification's, not a
    // simplification of it — a plain gamma of 2.2 shifts ratios enough to pass a colour that should fail.
    private static double Channel(double c) =>
        c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
}

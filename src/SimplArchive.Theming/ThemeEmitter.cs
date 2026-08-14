using System.Text;

namespace SimplArchive.Theming;

/// <summary>
/// Turns a palette into each client's own idea of a theme: an Avalonia resource dictionary and a CSS custom
/// property sheet (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// Two targets, one source. The desktop and the web have genuinely different theming machinery — Avalonia
/// resolves <c>{DynamicResource}</c> against typed brushes, the browser cascades custom properties — and no
/// abstraction over both would be simpler than writing the two files. What must not be duplicated is the
/// <em>decision</em> about which variables exist and what they are worth, and that lives in
/// <see cref="ThemeVariables"/>.
/// </para>
/// <para>
/// Output is deterministic to the byte, because a test regenerates it and compares against what is checked in.
/// That is the whole guarantee: generated files in the repository are a lie the moment somebody edits one by
/// hand, and this is what makes that fail the build instead of shipping.
/// </para>
/// </remarks>
public static class ThemeEmitter
{
    private const string DoNotEdit =
        "This file is GENERATED from src/SimplArchive.Theming/tokens.json by scripts/generate-theme.sh.\n"
        + "Edit the tokens and regenerate — a hand edit here fails ThemeGenerationTests on the next build.";

    /// <summary>The desktop client's theme dictionary: one <c>SolidColorBrush</c> per variable, per theme.</summary>
    public static string ToAvalonia(ThemeTokens tokens)
    {
        var xml = new StringBuilder();
        xml.Append("<!--\n     ").Append(DoNotEdit.Replace("\n", "\n     ")).Append("\n-->\n");
        xml.Append("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"\n");
        xml.Append("                    xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">\n");
        xml.Append("    <ResourceDictionary.ThemeDictionaries>\n");

        AppendAvaloniaTheme(xml, "Light", tokens.Light);
        AppendAvaloniaTheme(xml, "Dark", tokens.Dark);

        xml.Append("    </ResourceDictionary.ThemeDictionaries>\n");
        xml.Append("</ResourceDictionary>\n");
        return xml.ToString();
    }

    /// <summary>The web client's custom properties, in all four states the viewer's theme can be in.</summary>
    /// <remarks>
    /// The media query carries the operating system's preference and the <c>data-theme</c> rules carry an
    /// explicit choice, so the explicit one has to be able to win in <b>both</b> directions — which is why the
    /// light block is repeated under <c>[data-theme="light"]</c> rather than left to the default.
    /// </remarks>
    public static string ToCss(ThemeTokens tokens)
    {
        var css = new StringBuilder();
        css.Append("/*\n * ").Append(DoNotEdit.Replace("\n", "\n * ")).Append("\n */\n\n");

        AppendCssBlock(css, ":root", tokens.Light);
        css.Append('\n');
        css.Append("@media (prefers-color-scheme: dark) {\n");
        AppendCssBlock(css, "  :root", tokens.Dark, indent: "  ");
        css.Append("}\n\n");
        AppendCssBlock(css, ":root[data-theme=\"dark\"]", tokens.Dark);
        css.Append('\n');
        AppendCssBlock(css, ":root[data-theme=\"light\"]", tokens.Light);
        return css.ToString();
    }

    /// <summary>The user manual's colours (issue #513) — so a rebrand cannot strand the third styled surface.</summary>
    /// <remarks>
    /// The manual found out the hard way that it was a third styled surface: the teal flip (ADR 0578)
    /// regenerated every screenshot from the running app, and the Typst chrome around them kept its own
    /// hardcoded purple. Deliberately tiny — the manual consumes exactly one colour (headings, links, its
    /// darkened variants are computed in Typst), and print wants the LIGHT accent regardless of anybody's
    /// dark-mode preference, because paper is white.
    /// </remarks>
    public static string ToTypst(ThemeTokens tokens)
    {
        var typ = new StringBuilder();
        typ.Append("// ").Append(DoNotEdit.Replace("\n", "\n// ")).Append('\n');
        typ.Append("#let accent = rgb(\"").Append(tokens.Light.Accent.Primary).Append("\")\n");
        return typ.ToString();
    }

    private static void AppendAvaloniaTheme(StringBuilder xml, string theme, ThemePalette palette)
    {
        xml.Append("        <ResourceDictionary x:Key=\"").Append(theme).Append("\">\n");
        foreach (var variable in ThemeVariables.For(palette))
        {
            xml.Append("            <SolidColorBrush x:Key=\"").Append(variable.ResourceKey)
               .Append("\" Color=\"").Append(variable.Value).Append("\" />\n");
        }

        AppendFluentAccent(xml, palette);
        xml.Append("        </ResourceDictionary>\n");
    }

    // Avalonia's Fluent theme paints selection, hover and focus from SystemAccentColor and its six shades. Left
    // alone they stay the framework's blue, so a themed application ends up with the brand everywhere EXCEPT
    // the one place the eye follows — the selected row.
    private static void AppendFluentAccent(StringBuilder xml, ThemePalette palette)
    {
        var accent = palette.Accent.Primary;
        xml.Append("\n            <Color x:Key=\"SystemAccentColor\">").Append(accent).Append("</Color>\n");

        foreach (var step in new[] { 1, 2, 3 })
        {
            xml.Append("            <Color x:Key=\"SystemAccentColorLight").Append(step).Append("\">")
               .Append(AccentDerivation.Shade(accent, 0.08 * step)).Append("</Color>\n");
        }

        foreach (var step in new[] { 1, 2, 3 })
        {
            xml.Append("            <Color x:Key=\"SystemAccentColorDark").Append(step).Append("\">")
               .Append(AccentDerivation.Shade(accent, -0.08 * step)).Append("</Color>\n");
        }
    }

    private static void AppendCssBlock(StringBuilder css, string selector, ThemePalette palette, string indent = "")
    {
        css.Append(selector).Append(" {\n");
        foreach (var variable in ThemeVariables.For(palette))
        {
            css.Append(indent).Append("  ").Append(variable.CssName).Append(": ")
               .Append(ToCssColour(variable.Value)).Append(";\n");
        }

        css.Append(indent).Append("}\n");
    }

    // Avalonia writes alpha first (#AARRGGBB), CSS writes it last (#RRGGBBAA). The one translucent value we
    // emit is the empty-folder icon; everything else passes through untouched.
    private static string ToCssColour(string value) =>
        value.Length == 9 ? $"#{value[3..]}{value[1..3]}" : value;
}

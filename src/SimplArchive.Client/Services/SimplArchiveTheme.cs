using MudBlazor;
using SimplArchive.Theming;

namespace SimplArchive.Client.Services;

/// <summary>
/// Turns the design tokens into the <see cref="MudTheme"/> MudBlazor's components read (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// Until now the web client ran <c>new MudTheme()</c> — MudBlazor's stock theme, untouched, whose primary is
/// <c>#594AE2</c>. The desktop then hand-copied that same purple as <c>#5b4ee5</c> at eighteen sites. So the
/// product's visual identity was a UI library's default, replicated by hand into a second client: "it looks
/// like a template" was not an impression, it was the fact.
/// </para>
/// <para>
/// This is the third target of the one token source, alongside the Avalonia dictionary and the CSS custom
/// properties. It is <b>not</b> generated: MudBlazor's palette is a typed object with a hundred properties and
/// its own opinions about what a "surface" is, so mapping it is a judgement each time rather than a mechanical
/// emission — and a generator whose output nobody could read would be worse than a mapping that says which
/// token it chose and why.
/// </para>
/// </remarks>
public static class SimplArchiveTheme
{
    /// <summary>The shipped design, for the first render — before any installation override has arrived.</summary>
    public static MudTheme Shipped { get; } = From(ThemeTokensReader.Shipped);

    /// <summary>A theme MudBlazor can wear, from a palette we understand.</summary>
    public static MudTheme From(ThemeTokens tokens) => new()
    {
        PaletteLight = Palette(tokens.Light),
        PaletteDark = PaletteDark(tokens.Dark),
    };

    private static PaletteLight Palette(ThemePalette palette) => new()
    {
        Primary = palette.Accent.Primary,
        PrimaryContrastText = palette.Accent.OnPrimary,
        Secondary = palette.Accent.Text,
        Success = palette.Semantic.Success,
        Warning = palette.Semantic.Warning,
        Error = palette.Semantic.Danger,
        Info = palette.Accent.Text,

        Background = palette.Neutral.Canvas,
        Surface = palette.Neutral.Surface,
        // MudBlazor's "grey" backgrounds are what its tables, toolbars and hovers reach for — the same role the
        // desktop calls sunken, so they take the same token rather than a shade MudBlazor picked.
        BackgroundGray = palette.Neutral.Sunken,
        DrawerBackground = palette.Neutral.Surface,
        AppbarBackground = palette.Neutral.Surface,
        AppbarText = palette.Neutral.Text,

        TextPrimary = palette.Neutral.Text,
        TextSecondary = palette.Neutral.TextSecondary,
        TextDisabled = palette.Neutral.TextFaint,
        ActionDefault = palette.Neutral.TextSecondary,
        ActionDisabled = palette.Neutral.TextFaint,

        Divider = palette.Neutral.Hairline,
        DividerLight = palette.Neutral.Hairline,
        LinesDefault = palette.Neutral.Hairline,
        LinesInputs = palette.Neutral.Hairline,
        TableLines = palette.Neutral.Hairline,
    };

    // The same mapping, on MudBlazor's dark palette type. Two nearly-identical methods rather than one generic
    // one because PaletteLight and PaletteDark are unrelated types with no shared writable interface — a
    // reflection-driven copy would be shorter to write and impossible to read.
    private static PaletteDark PaletteDark(ThemePalette palette) => new()
    {
        Primary = palette.Accent.Primary,
        PrimaryContrastText = palette.Accent.OnPrimary,
        Secondary = palette.Accent.Text,
        Success = palette.Semantic.Success,
        Warning = palette.Semantic.Warning,
        Error = palette.Semantic.Danger,
        Info = palette.Accent.Text,

        Background = palette.Neutral.Canvas,
        Surface = palette.Neutral.Surface,
        BackgroundGray = palette.Neutral.Sunken,
        DrawerBackground = palette.Neutral.Surface,
        AppbarBackground = palette.Neutral.Surface,
        AppbarText = palette.Neutral.Text,

        TextPrimary = palette.Neutral.Text,
        TextSecondary = palette.Neutral.TextSecondary,
        TextDisabled = palette.Neutral.TextFaint,
        ActionDefault = palette.Neutral.TextSecondary,
        ActionDisabled = palette.Neutral.TextFaint,

        Divider = palette.Neutral.Hairline,
        DividerLight = palette.Neutral.Hairline,
        LinesDefault = palette.Neutral.Hairline,
        LinesInputs = palette.Neutral.Hairline,
        TableLines = palette.Neutral.Hairline,
    };
}

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
        Typography = Typography(),
    };

    /// <summary>
    /// The font stack, stated rather than inherited (issue #844).
    /// </summary>
    /// <remarks>
    /// The component library's default typography asks for a font this app never shipped, which the page used to
    /// fetch from a third-party font CDN. That was worth removing on three counts, none of them typographic: it
    /// sent every visitor's address to a third party on every page load — awkward for a product whose default
    /// region is chosen for data residency; it made an air-gapped installation depend on reaching the public
    /// internet; and it forced the content-security policy to name an external origin (ADR 0084).
    ///
    /// The stack below is what <c>app.css</c> already declared for the page body, so this makes the two agree
    /// rather than changing the design: previously the components asked for the CDN font and the body did not.
    /// </remarks>
    private static Typography Typography()
    {
        string[] stack = ["Helvetica Neue", "Helvetica", "Arial", "sans-serif"];
        return new Typography
        {
            Default = new DefaultTypography { FontFamily = stack },
            H1 = new H1Typography { FontFamily = stack },
            H2 = new H2Typography { FontFamily = stack },
            H3 = new H3Typography { FontFamily = stack },
            H4 = new H4Typography { FontFamily = stack },
            H5 = new H5Typography { FontFamily = stack },
            H6 = new H6Typography { FontFamily = stack },
            Subtitle1 = new Subtitle1Typography { FontFamily = stack },
            Subtitle2 = new Subtitle2Typography { FontFamily = stack },
            Body1 = new Body1Typography { FontFamily = stack },
            Body2 = new Body2Typography { FontFamily = stack },
            Button = new ButtonTypography { FontFamily = stack },
            Caption = new CaptionTypography { FontFamily = stack },
            Overline = new OverlineTypography { FontFamily = stack },
        };
    }

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

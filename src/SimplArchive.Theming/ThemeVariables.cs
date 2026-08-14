namespace SimplArchive.Theming;

/// <summary>
/// The one list of what a palette actually exposes to a client, and what each entry is called on each side.
/// </summary>
/// <remarks>
/// <para>
/// Defined once because the alternative is two lists that agree until they don't: a token added for the web and
/// forgotten in the Avalonia dictionary is a control that silently keeps the old colour, which nobody notices
/// until a customer's brand is half-applied. The emitters below decide <em>syntax</em> only — never which
/// variables exist.
/// </para>
/// <para>
/// The <c>Wb*</c> keys are the desktop client's existing names (WbChrome, WbBorder, WbMuted, WbFaint,
/// WbFolder, WbFolderEmpty), kept exactly so the XAML that already binds to them keeps working. The rest are
/// new. Renaming the old four would have been tidier and would have touched every view in the client for no
/// behaviour — a trade worth refusing.
/// </para>
/// </remarks>
public static class ThemeVariables
{
    /// <summary>How transparent an empty folder's icon is drawn — the desktop's long-standing 55%.</summary>
    private const string EmptyFolderAlpha = "8c";

    /// <summary>Every variable in one palette: the Avalonia resource key, the CSS name, and the value.</summary>
    public static IReadOnlyList<ThemeVariable> For(ThemePalette palette) =>
    [
        // Neutrals. WbChrome/WbBorder/WbMuted/WbFaint are the pre-existing keys.
        new("WbCanvas", "--sa-canvas", palette.Neutral.Canvas),
        new("WbSurface", "--sa-surface", palette.Neutral.Surface),
        new("WbChrome", "--sa-sunken", palette.Neutral.Sunken),
        new("WbBorder", "--sa-hairline", palette.Neutral.Hairline),
        new("WbText", "--sa-text", palette.Neutral.Text),
        new("WbMuted", "--sa-text-secondary", palette.Neutral.TextSecondary),
        new("WbFaint", "--sa-text-faint", palette.Neutral.TextFaint),
        new("WbFolder", "--sa-folder", palette.Neutral.Folder),
        new("WbFolderEmpty", "--sa-folder-empty", Translucent(palette.Neutral.Folder)),

        // The accent, in the only roles it is allowed to hold.
        new("WbAccent", "--sa-accent", palette.Accent.Primary),
        new("WbAccentHover", "--sa-accent-hover", palette.Accent.Hover),
        new("WbOnAccent", "--sa-on-accent", palette.Accent.OnPrimary),
        new("WbAccentText", "--sa-accent-text", palette.Accent.Text),
        new("WbAccentTint", "--sa-accent-tint", palette.Accent.Tint),
        new("WbSelection", "--sa-selection", palette.Accent.Selection),

        // Meaning, never brand.
        new("WbDanger", "--sa-danger", palette.Semantic.Danger),
        new("WbSuccess", "--sa-success", palette.Semantic.Success),
        new("WbWarning", "--sa-warning", palette.Semantic.Warning),
    ];

    // Avalonia takes #AARRGGBB; CSS takes #RRGGBBAA. Emitted in Avalonia's order here and flipped by the CSS
    // emitter, because the desktop is the only side that consumes this one today.
    private static string Translucent(string hex) => $"#{EmptyFolderAlpha}{hex[1..]}";
}

/// <summary>One themed value, under the name each client knows it by.</summary>
public sealed record ThemeVariable(string ResourceKey, string CssName, string Value);

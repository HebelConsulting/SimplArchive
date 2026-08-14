using System.Text.Json.Serialization;

namespace SimplArchive.Theming;

/// <summary>
/// The whole design in one object: a light palette and a dark one (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// Both themes are stated rather than one being derived from the other. Deriving a dark palette by inverting a
/// light one produces colours that are technically opposite and visually wrong — a saturated accent that reads
/// well on white is usually too dark on near-black, and the fix is a lighter, less saturated sibling rather
/// than an inversion of the same value.
/// </para>
/// <para>
/// Colours are kept as <c>#RRGGBB</c> strings all the way through. Each target wants a different concrete
/// type — an Avalonia <c>Color</c>, a CSS declaration, a MudBlazor palette string — so parsing here would only
/// mean converting back for two of the three.
/// </para>
/// </remarks>
public sealed record ThemeTokens(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("light")] ThemePalette Light,
    [property: JsonPropertyName("dark")] ThemePalette Dark);

public sealed record ThemePalette(
    [property: JsonPropertyName("accent")] AccentTokens Accent,
    [property: JsonPropertyName("semantic")] SemanticTokens Semantic,
    [property: JsonPropertyName("neutral")] NeutralTokens Neutral);

/// <summary>The accent, in the handful of roles it is allowed to appear in.</summary>
/// <remarks>
/// The set is deliberately this short. An accent that has a token for every possible use ends up used
/// everywhere, which is exactly the state the redesign is undoing: <c>#5b4ee5</c> was the foreground of every
/// icon in the desktop client, so the whole application read purple.
/// </remarks>
public sealed record AccentTokens(
    [property: JsonPropertyName("primary")] string Primary,
    [property: JsonPropertyName("hover")] string Hover,
    [property: JsonPropertyName("onPrimary")] string OnPrimary,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("tint")] string Tint,
    [property: JsonPropertyName("selection")] string Selection);

/// <summary>Meaning, not decoration: these say what happened, and are never the brand.</summary>
public sealed record SemanticTokens(
    [property: JsonPropertyName("danger")] string Danger,
    [property: JsonPropertyName("success")] string Success,
    [property: JsonPropertyName("warning")] string Warning);

/// <summary>
/// The greys, plus the folder gold. <b>Not overridable</b> — see <see cref="ThemeTokensReader"/>.
/// </summary>
public sealed record NeutralTokens(
    [property: JsonPropertyName("canvas")] string Canvas,
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("sunken")] string Sunken,
    [property: JsonPropertyName("hairline")] string Hairline,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("textSecondary")] string TextSecondary,
    [property: JsonPropertyName("textFaint")] string TextFaint,
    [property: JsonPropertyName("folder")] string Folder);

using System.Reflection;
using System.Text.Json;

namespace SimplArchive.Theming;

/// <summary>
/// The shipped design, and how a customer's own colours are laid over it (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// <b>The shipped theme is embedded, not read from disk.</b> There is no file to lose, no folder to forget in a
/// container image, and no first-run state where the product has no colours. A custom theme is an
/// <i>override</i> in the literal sense: it starts from what shipped and replaces named values.
/// </para>
/// <para>
/// <b>Only the accent and the semantic colours can be overridden.</b> The neutrals carry legibility — a badly
/// chosen surface/text pair is unreadable in a way no accent can be — and the geometry (spacing, radii, the
/// type scale) is layout, where a wrong value is a broken window rather than an ugly one. A custom file that
/// contains neutrals is not rejected for it; the neutrals are ignored and said so in the log, because the
/// likeliest reason they are there is that somebody copied the whole shipped file as a starting point.
/// </para>
/// </remarks>
public static class ThemeTokensReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly Lazy<ThemeTokens> ShippedTokens = new(ReadShipped);

    /// <summary>The design as it ships. Always available, with or without any file on any filesystem.</summary>
    public static ThemeTokens Shipped => ShippedTokens.Value;

    /// <summary>What a custom theme file was: accepted, ignored, or rejected and why.</summary>
    /// <param name="Tokens">Always usable — the merged theme, or the shipped one when the override was refused.</param>
    /// <param name="Applied">False when the shipped design is what you are getting.</param>
    /// <param name="Notes">
    /// Everything worth telling an administrator, whether or not the theme was applied: the contrast ratio that
    /// failed, the section that was ignored, the property that was not a colour.
    /// </param>
    public sealed record ThemeLoad(ThemeTokens Tokens, bool Applied, IReadOnlyList<string> Notes);

    /// <summary>
    /// Lays a custom theme document over the shipped one. Never throws and never returns an unusable theme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The smallest useful custom theme is one colour.</b> Give a light <c>primary</c> and everything
    /// else — hover, the colour of text on a filled button, the tint, the selection background, and the whole
    /// dark palette — is derived from it (see <see cref="AccentDerivation"/>). Anything stated explicitly wins.
    /// </para>
    /// <para>
    /// The dark section may therefore be omitted entirely, and usually should be: a brand colour chosen against
    /// white is nearly always too dark against near-black, and lifting it is a mechanical job nobody should
    /// have to do by hand.
    /// </para>
    /// </remarks>
    public static ThemeLoad Load(string json)
    {
        var notes = new List<string>();

        CustomTheme? custom;
        try
        {
            custom = JsonSerializer.Deserialize<CustomTheme>(json, Options);
        }
        catch (JsonException e)
        {
            return new ThemeLoad(Shipped, false, [$"The custom theme is not valid JSON and was ignored: {e.Message}"]);
        }

        if (custom is null || (custom.Light is null && custom.Dark is null))
        {
            return new ThemeLoad(Shipped, false, ["The custom theme contains no light or dark section and was ignored."]);
        }

        if (custom.Light?.Neutral is not null || custom.Dark?.Neutral is not null)
        {
            notes.Add(
                "The custom theme sets neutral colours. Those are not overridable — they carry legibility — so "
                + "they were ignored and the shipped neutrals are in use.");
        }

        // Every supplied value has to be a colour BEFORE anything is derived from it. Derivation converts to
        // HSL and back, which on "teal" or "#FFF" is an exception rather than a bad palette — and a theme file
        // must never be able to take the application down with it.
        var malformed = Supplied(custom).Where(v => !Contrast.IsColour(v.Value)).ToList();
        if (malformed.Count > 0)
        {
            notes.AddRange(malformed.Select(v => $"{v.Name} is '{v.Value}', which is not a #RRGGBB colour."));
            notes.Add("The custom theme was refused and the shipped design is in use.");
            return new ThemeLoad(Shipped, false, notes);
        }

        // A custom theme can be a single colour. Everything unstated is DERIVED from the customer's own
        // primary rather than merged over the shipped teal — merging would give them their buttons and our
        // links, tints and selection, which is a half-applied brand that looks like a bug.
        //
        // The dark primary, when unstated, is lifted out of the light one until it is readable on a dark
        // surface. A brand colour picked against white is usually far too dark against near-black, so the
        // alternative — demanding a second variant — means most customers supply nothing and get refused.
        var lightPrimary = custom.Light?.Accent?.Primary;
        var darkPrimary = custom.Dark?.Accent?.Primary;
        var anyPrimary = lightPrimary ?? darkPrimary;

        // With a primary to work from, the accent is derived and then overlaid; without one, there is nothing
        // to derive from, so an override that names only (say) a hover colour still edits the shipped accent.
        var lightAccent = Overlay(
            anyPrimary is null
                ? Shipped.Light.Accent
                : AccentDerivation.From(lightPrimary ?? anyPrimary, Shipped.Light.Neutral.Surface, dark: false),
            custom.Light?.Accent);

        var darkAccent = Overlay(
            anyPrimary is null
                ? Shipped.Dark.Accent
                : AccentDerivation.From(darkPrimary ?? anyPrimary, Shipped.Dark.Neutral.Surface, dark: true),
            custom.Dark?.Accent);

        var lightSemantic = Merge(Shipped.Light.Semantic, custom.Light?.Semantic);
        var darkSemantic = Merge(Shipped.Dark.Semantic, custom.Dark?.Semantic ?? custom.Light?.Semantic);

        var merged = new ThemeTokens(
            string.IsNullOrWhiteSpace(custom.Name) ? Shipped.Name : custom.Name!,
            Shipped.Light with { Accent = lightAccent, Semantic = lightSemantic },
            Shipped.Dark with { Accent = darkAccent, Semantic = darkSemantic });

        var failures = Validate(merged).ToList();
        if (failures.Count > 0)
        {
            notes.AddRange(failures);
            notes.Add("The custom theme was refused and the shipped design is in use.");
            return new ThemeLoad(Shipped, false, notes);
        }

        return new ThemeLoad(merged, true, notes);
    }

    /// <summary>
    /// Every reason a theme cannot go on screen — an empty result means it can.
    /// </summary>
    /// <remarks>
    /// Applied to the shipped palette by a unit test as well as to every override, so the standard cannot
    /// quietly become "everyone except us".
    /// </remarks>
    public static IEnumerable<string> Validate(ThemeTokens tokens)
    {
        foreach (var failure in Validate(tokens.Light, "light"))
        {
            yield return failure;
        }

        foreach (var failure in Validate(tokens.Dark, "dark"))
        {
            yield return failure;
        }
    }

    private static IEnumerable<string> Validate(ThemePalette palette, string theme)
    {
        var colours = new (string Name, string Value)[]
        {
            ($"{theme}.accent.primary", palette.Accent.Primary),
            ($"{theme}.accent.hover", palette.Accent.Hover),
            ($"{theme}.accent.onPrimary", palette.Accent.OnPrimary),
            ($"{theme}.accent.text", palette.Accent.Text),
            ($"{theme}.accent.tint", palette.Accent.Tint),
            ($"{theme}.accent.selection", palette.Accent.Selection),
            ($"{theme}.semantic.danger", palette.Semantic.Danger),
            ($"{theme}.semantic.success", palette.Semantic.Success),
            ($"{theme}.semantic.warning", palette.Semantic.Warning),
        };

        foreach (var (name, value) in colours)
        {
            if (!Contrast.IsColour(value))
            {
                yield return $"{name} is '{value}', which is not a #RRGGBB colour.";
                yield break; // the ratios below would be arithmetic on nonsense
            }
        }

        // Label text on a filled primary button.
        foreach (var failure in AtLeastAa(
            $"{theme}.accent.onPrimary", palette.Accent.OnPrimary, palette.Accent.Primary, "on the primary button"))
        {
            yield return failure;
        }

        // The accent used AS text — links, the active toolbar icon, a chip's label.
        foreach (var failure in AtLeastAa(
            $"{theme}.accent.text", palette.Accent.Text, palette.Neutral.Surface, "as text on the surface"))
        {
            yield return failure;
        }

        foreach (var (name, value) in new[]
                 {
                     ($"{theme}.semantic.danger", palette.Semantic.Danger),
                     ($"{theme}.semantic.success", palette.Semantic.Success),
                     ($"{theme}.semantic.warning", palette.Semantic.Warning),
                 })
        {
            foreach (var failure in AtLeastAa(name, value, palette.Neutral.Surface, "as text on the surface"))
            {
                yield return failure;
            }
        }
    }

    private static IEnumerable<string> AtLeastAa(string name, string foreground, string background, string role)
    {
        var ratio = Contrast.Between(foreground, background);
        if (ratio < Contrast.MinimumAa)
        {
            yield return
                $"{name} ({foreground}) has a contrast ratio of {ratio:0.00}:1 against {background} {role}; "
                + $"WCAG AA needs at least {Contrast.MinimumAa:0.0}:1. Choose a darker or lighter shade.";
        }
    }

    // Every colour the file actually states, named the way the notes report it.
    private static IEnumerable<(string Name, string Value)> Supplied(CustomTheme custom)
    {
        foreach (var (theme, palette) in new[] { ("light", custom.Light), ("dark", custom.Dark) })
        {
            if (palette?.Accent is { } accent)
            {
                foreach (var (name, value) in new (string, string?)[]
                         {
                             ("primary", accent.Primary), ("hover", accent.Hover), ("onPrimary", accent.OnPrimary),
                             ("text", accent.Text), ("tint", accent.Tint), ("selection", accent.Selection),
                         })
                {
                    if (value is not null)
                    {
                        yield return ($"{theme}.accent.{name}", value);
                    }
                }
            }

            if (palette?.Semantic is { } semantic)
            {
                foreach (var (name, value) in new (string, string?)[]
                         {
                             ("danger", semantic.Danger), ("success", semantic.Success), ("warning", semantic.Warning),
                         })
                {
                    if (value is not null)
                    {
                        yield return ($"{theme}.semantic.{name}", value);
                    }
                }
            }
        }
    }

    // Explicit values always win over derived ones — this only fills silence.
    private static AccentTokens Overlay(AccentTokens derived, CustomAccent? custom) =>
        custom is null
            ? derived
            : new AccentTokens(
                custom.Primary ?? derived.Primary,
                custom.Hover ?? derived.Hover,
                custom.OnPrimary ?? derived.OnPrimary,
                custom.Text ?? derived.Text,
                custom.Tint ?? derived.Tint,
                custom.Selection ?? derived.Selection);

    private static SemanticTokens Merge(SemanticTokens shipped, CustomSemantic? custom) =>
        custom is null
            ? shipped
            : new SemanticTokens(
                custom.Danger ?? shipped.Danger,
                custom.Success ?? shipped.Success,
                custom.Warning ?? shipped.Warning);

    private static ThemeTokens ReadShipped()
    {
        using var stream = typeof(ThemeTokensReader).Assembly
            .GetManifestResourceStream("SimplArchive.Theming.tokens.json")
            ?? throw new InvalidOperationException(
                "tokens.json is missing from SimplArchive.Theming. It is the shipped design and is embedded "
                + "deliberately, so this means the build no longer includes it as an EmbeddedResource.");

        return JsonSerializer.Deserialize<ThemeTokens>(stream, Options)
            ?? throw new InvalidOperationException("The embedded tokens.json could not be read as a theme.");
    }

    // A custom file states only what it changes, so every property is optional — which is a different shape
    // from ThemeTokens, where everything is present by construction.
    private sealed record CustomTheme(string? Name, CustomPalette? Light, CustomPalette? Dark);

    private sealed record CustomPalette(CustomAccent? Accent, CustomSemantic? Semantic, JsonElement? Neutral);

    private sealed record CustomAccent(
        string? Primary, string? Hover, string? OnPrimary, string? Text, string? Tint, string? Selection);

    private sealed record CustomSemantic(string? Danger, string? Success, string? Warning);
}

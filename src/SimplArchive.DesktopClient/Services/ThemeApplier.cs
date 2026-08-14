using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using SimplArchive.Theming;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Puts a chosen style on screen — at startup, and again whenever the user picks a different one (ADR 0578).
/// </summary>
/// <remarks>
/// <para>
/// The generated <c>Themes/Tokens.axaml</c> is the shipped design and stays merged as the base. Applying a
/// style adds a second dictionary <em>after</em> it, so every brush it names wins and everything it does not
/// name still resolves. Switching styles therefore means replacing one dictionary rather than rebuilding the
/// application's resources, and switching <b>back</b> means removing it.
/// </para>
/// <para>
/// The brushes are <c>{DynamicResource}</c> everywhere in the views precisely so this works while the window is
/// open: a user who changes style in the server-profile editor sees the workbench behind it change, which is
/// the only honest way to choose a colour.
/// </para>
/// </remarks>
public static class ThemeApplier
{
    // Named so it can be found and removed again. Anonymous dictionaries would accumulate one per switch, and
    // the fifth style someone tried would still be sitting under the one they can see.
    private const string OverrideName = "SimplArchive.StyleOverride";

    /// <summary>What happened, for the log — never shown to the user as an interruption.</summary>
    public static IReadOnlyList<string> Apply(string? styleId)
    {
        if (Application.Current is not { } application)
        {
            return []; // headless verification hooks that never build an application
        }

        var load = ThemeCatalog.Load(styleId);
        Remove(application);

        if (load.Applied)
        {
            application.Resources.MergedDictionaries.Add(Build(load.Tokens));
        }

        // Fluent does NOT take its accent from the SystemAccentColor resource — it takes it from its own
        // ColorPaletteResources, seeded from the PLATFORM accent (macOS blue, here). Setting the resource key
        // alone leaves every accent-styled control — the Save button, a focus ring — wearing the operating
        // system's colour while everything we style ourselves wears the brand. Measured, not assumed: the
        // resource was in place and the button stayed blue.
        SetFluentAccent(application, load.Applied ? load.Tokens : ThemeTokensReader.Shipped);

        return load.Notes;
    }

    private static void SetFluentAccent(Application application, ThemeTokens tokens)
    {
        foreach (var fluent in application.Styles.OfType<FluentTheme>())
        {
            Accent(fluent, ThemeVariant.Light, tokens.Light.Accent.Primary);
            Accent(fluent, ThemeVariant.Dark, tokens.Dark.Accent.Primary);
        }

        static void Accent(FluentTheme fluent, ThemeVariant variant, string colour)
        {
            if (fluent.Palettes.TryGetValue(variant, out var palette))
            {
                palette.Accent = Color.Parse(colour);
            }
            else
            {
                fluent.Palettes[variant] = new ColorPaletteResources { Accent = Color.Parse(colour) };
            }
        }
    }

    private static void Remove(Application application)
    {
        var existing = application.Resources.MergedDictionaries
            .OfType<ResourceDictionary>()
            .FirstOrDefault(d => d.TryGetValue(OverrideName, out _));

        if (existing is not null)
        {
            application.Resources.MergedDictionaries.Remove(existing);
        }
    }

    // Mirrors the generated dictionary's shape — a ThemeDictionaries pair — so a style follows the light/dark
    // switch exactly as the shipped design does, rather than freezing whichever variant was current when it
    // was applied.
    private static ResourceDictionary Build(ThemeTokens tokens)
    {
        var dictionary = new ResourceDictionary { [OverrideName] = true };
        dictionary.ThemeDictionaries[ThemeVariant.Light] = Palette(tokens.Light);
        dictionary.ThemeDictionaries[ThemeVariant.Dark] = Palette(tokens.Dark);
        return dictionary;
    }

    private static ResourceDictionary Palette(ThemePalette palette)
    {
        var dictionary = new ResourceDictionary();
        foreach (var variable in ThemeVariables.For(palette))
        {
            dictionary[variable.ResourceKey] = new SolidColorBrush(Color.Parse(variable.Value));
        }

        // Fluent paints selection, hover and focus from these; without them a themed application keeps the
        // framework's blue in the one place the eye follows.
        var accent = palette.Accent.Primary;
        dictionary["SystemAccentColor"] = Color.Parse(accent);
        for (var step = 1; step <= 3; step++)
        {
            dictionary[$"SystemAccentColorLight{step}"] = Color.Parse(AccentDerivation.Shade(accent, 0.08 * step));
            dictionary[$"SystemAccentColorDark{step}"] = Color.Parse(AccentDerivation.Shade(accent, -0.08 * step));
        }

        return dictionary;
    }
}

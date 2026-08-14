using SimplArchive.ThemeGen;
using SimplArchive.Theming;

namespace SimplArchive.UnitTests;

/// <summary>
/// The design tokens, the generator's output, and what a custom theme is allowed to do (ADR 0578).
/// </summary>
public class ThemeTokenTests
{
    /// <summary>
    /// What is checked in equals what the generator would write. Without this, the generated files are a claim
    /// rather than a fact — and the failure mode is a hand edit that survives until somebody regenerates months
    /// later and cannot tell which version was intended.
    /// </summary>
    [Fact]
    public void The_generated_theme_files_are_in_step_with_the_tokens()
    {
        var root = RepoRoot();

        foreach (var (path, expected) in ThemeOutputs.For(root))
        {
            var relative = Path.GetRelativePath(root, path);
            Assert.True(File.Exists(path), $"{relative} is missing — run scripts/generate-theme.sh.");
            Assert.True(
                File.ReadAllText(path) == expected,
                $"{relative} does not match what the tokens produce. Either a token changed without "
                + "regenerating, or this file was edited by hand. Run scripts/generate-theme.sh.");
        }
    }

    /// <summary>
    /// The shipped palette meets the same contrast bar every custom theme is held to. A rule the product itself
    /// fails is a rule that gets waived the first time it is inconvenient.
    /// </summary>
    [Fact]
    public void The_shipped_theme_passes_its_own_contrast_rules()
    {
        var failures = ThemeTokensReader.Validate(ThemeTokensReader.Shipped).ToList();

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The alternates shipped in the desktop packages are real, applicable themes — not sample files that would
    /// be refused the moment somebody picked one. classic-purple.json is the escape hatch: the identity the
    /// product wore before the redesign, on the new chassis, one pick away — which is what made shipping teal
    /// by default a reversible decision rather than a bet. production/integration/development are the
    /// ENVIRONMENT set: an administrator with three near-identical windows open needs to know at a glance
    /// which system they are about to change, and each states a single colour so the derivation builds the
    /// rest and the contrast gate proves it.
    /// </summary>
    [Theory]
    [InlineData("indigo.json")]
    [InlineData("ink-blue.json")]
    [InlineData("classic-purple.json")]
    [InlineData("production.json")]
    [InlineData("integration.json")]
    [InlineData("development.json")]
    public void Each_shipped_preset_applies_and_passes_contrast(string file)
    {
        var path = Path.Combine(RepoRoot(), "src", "SimplArchive.Theming", "presets", file);
        Assert.True(File.Exists(path), $"{file} is missing — the desktop packages ship it as a rename-to-activate theme.");

        var load = ThemeTokensReader.Load(File.ReadAllText(path));

        Assert.True(load.Applied, string.Join("\n", load.Notes));
        Assert.Empty(load.Notes);
        Assert.NotEqual(ThemeTokensReader.Shipped.Light.Accent.Primary, load.Tokens.Light.Accent.Primary);
    }

    /// <summary>
    /// One colour is a complete theme: the rest of the accent is derived from it, so nothing is left wearing
    /// the shipped teal. A half-applied brand — their buttons, our links — looks like a bug and is nobody's
    /// design.
    /// </summary>
    [Fact]
    public void A_single_colour_is_enough_for_a_whole_accent()
    {
        var load = ThemeTokensReader.Load("""
            { "name": "Acme", "light": { "accent": { "primary": "#7A0F52" } } }
            """);

        Assert.True(load.Applied, string.Join("\n", load.Notes));
        Assert.Equal("Acme", load.Tokens.Name);
        Assert.Equal("#7A0F52", load.Tokens.Light.Accent.Primary);

        // Derived from theirs — emphatically NOT the shipped teal's.
        var shipped = ThemeTokensReader.Shipped.Light.Accent;
        Assert.NotEqual(shipped.Tint, load.Tokens.Light.Accent.Tint);
        Assert.NotEqual(shipped.Selection, load.Tokens.Light.Accent.Selection);
        Assert.NotEqual(shipped.Hover, load.Tokens.Light.Accent.Hover);
        Assert.Equal("#FFFFFF", load.Tokens.Light.Accent.OnPrimary); // white wins on a dark aubergine

        // Not part of the accent, so still exactly as shipped.
        Assert.Equal(ThemeTokensReader.Shipped.Light.Neutral.Surface, load.Tokens.Light.Neutral.Surface);
        Assert.Equal(ThemeTokensReader.Shipped.Light.Semantic.Danger, load.Tokens.Light.Semantic.Danger);
    }

    /// <summary>An explicit value always beats a derived one — derivation only fills silence.</summary>
    [Fact]
    public void An_explicit_value_wins_over_the_derived_one()
    {
        var load = ThemeTokensReader.Load("""
            { "light": { "accent": { "primary": "#7A0F52", "tint": "#FDF2F8" } } }
            """);

        Assert.True(load.Applied, string.Join("\n", load.Notes));
        Assert.Equal("#FDF2F8", load.Tokens.Light.Accent.Tint);
    }

    /// <summary>
    /// A theme with only a light section still gets a dark one, <b>lifted</b> from its own colour rather than
    /// copied. Copying is what the first version did, and it made every dark brand colour fail validation: a
    /// deep aubergine that reads beautifully on white scores 1.68:1 on near-black, so a customer who set one
    /// colour got the shipped teal and a message about a mode they never configured.
    /// </summary>
    [Fact]
    public void A_light_only_override_is_lifted_for_dark_mode()
    {
        const string brand = "#7A0F52";
        var load = ThemeTokensReader.Load($$"""
            { "light": { "accent": { "primary": "{{brand}}" } } }
            """);

        Assert.True(load.Applied, string.Join("\n", load.Notes));

        var dark = load.Tokens.Dark.Accent.Primary;
        Assert.NotEqual(brand, dark);
        Assert.NotEqual(ThemeTokensReader.Shipped.Dark.Accent.Primary, dark);

        // Lighter than the light-mode colour (contrast against black rises with luminance) and readable on the
        // dark surface, which is the whole point of lifting it.
        Assert.True(Contrast.Between(dark, "#000000") > Contrast.Between(brand, "#000000"));
        Assert.True(Contrast.Between(dark, load.Tokens.Dark.Neutral.Surface) >= Contrast.MinimumAa);
    }

    /// <summary>
    /// The whole reason the gate exists: somebody's corporate pale yellow. It is refused with the measured
    /// ratio, and the shipped design stays on screen — a product that looks broken reads as our fault.
    /// </summary>
    [Fact]
    public void An_unreadable_accent_is_refused_with_its_measured_ratio()
    {
        var load = ThemeTokensReader.Load("""
            { "light": { "accent": { "primary": "#F5E663", "onPrimary": "#FFFFFF" } } }
            """);

        Assert.False(load.Applied);
        Assert.Same(ThemeTokensReader.Shipped, load.Tokens);
        Assert.Contains(load.Notes, n => n.Contains("contrast ratio") && n.Contains("light.accent.onPrimary"));
    }

    /// <summary>
    /// Neutrals are not overridable, and a file containing them is not an error — the likeliest reason they are
    /// there is that somebody copied the shipped file as a starting point.
    /// </summary>
    [Fact]
    public void Neutrals_in_an_override_are_ignored_and_said_so()
    {
        var load = ThemeTokensReader.Load("""
            {
              "light": {
                "accent": { "primary": "#7A0F52", "onPrimary": "#FFFFFF", "text": "#7A0F52" },
                "neutral": { "surface": "#000000" }
              }
            }
            """);

        Assert.True(load.Applied, string.Join("\n", load.Notes));
        Assert.Equal(ThemeTokensReader.Shipped.Light.Neutral.Surface, load.Tokens.Light.Neutral.Surface);
        Assert.Contains(load.Notes, n => n.Contains("neutral", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Nonsense in the file must cost the colours, never the application.</summary>
    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("{}")]
    [InlineData("""{ "light": { "accent": { "primary": "teal" } } }""")]
    public void A_broken_override_falls_back_to_the_shipped_design(string json)
    {
        var load = ThemeTokensReader.Load(json);

        Assert.False(load.Applied);
        Assert.Same(ThemeTokensReader.Shipped, load.Tokens);
        Assert.NotEmpty(load.Notes);
    }

    /// <summary>
    /// The two clients must expose the same variables. A token added for one and forgotten in the other is a
    /// control that silently keeps the old colour — half an applied brand, and nobody notices for months.
    /// </summary>
    [Fact]
    public void Both_clients_receive_the_same_set_of_variables()
    {
        var light = ThemeVariables.For(ThemeTokensReader.Shipped.Light);
        var dark = ThemeVariables.For(ThemeTokensReader.Shipped.Dark);

        Assert.Equal(light.Select(v => v.ResourceKey), dark.Select(v => v.ResourceKey));
        Assert.Equal(light.Count, light.Select(v => v.ResourceKey).Distinct().Count());
        Assert.Equal(light.Count, light.Select(v => v.CssName).Distinct().Count());
        Assert.All(light, v => Assert.StartsWith("--sa-", v.CssName));
    }

    /// <summary>Known ratios, so a broken luminance formula cannot pass by agreeing with itself.</summary>
    [Fact]
    public void Contrast_matches_the_published_reference_values()
    {
        Assert.Equal(21.0, Contrast.Between("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, Contrast.Between("#7A0F52", "#7A0F52"), 2);
        Assert.Equal(Contrast.Between("#FFFFFF", "#0F766E"), Contrast.Between("#0F766E", "#FFFFFF"), 6);

        Assert.False(Contrast.IsColour("teal"));
        Assert.False(Contrast.IsColour("#FFF"));
        Assert.True(Contrast.IsColour("#0F766E"));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}

using SimplArchive.Theming;

namespace SimplArchive.ThemeGen;

/// <summary>
/// Which generated files exist, where they go, and what should be in them (ADR 0578).
/// </summary>
/// <remarks>
/// Shared by the generator and by <c>ThemeGenerationTests</c>, which is the entire point: the test asserts
/// that what is checked in equals what this would write, so the two must be reading from one list. Two lists
/// would mean the guard could pass while a file it does not know about rots.
/// </remarks>
public static class ThemeOutputs
{
    /// <summary>Absolute path → the exact content it should hold.</summary>
    public static IReadOnlyList<(string Path, string Content)> For(string repoRoot) =>
    [
        (Path.Combine(repoRoot, "src", "SimplArchive.DesktopClient", "Themes", "Tokens.axaml"),
            ThemeEmitter.ToAvalonia(ThemeTokensReader.Shipped)),

        (Path.Combine(repoRoot, "src", "SimplArchive.Client", "wwwroot", "css", "tokens.generated.css"),
            ThemeEmitter.ToCss(ThemeTokensReader.Shipped)),
    ];
}

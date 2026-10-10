namespace SimplArchive.Localization;

/// <summary>A user-interface language a client offers at sign-in.</summary>
public sealed record LanguageOption(string Name, string Code);

/// <summary>
/// The languages every client offers at sign-in (ADR 0468), in one place so the desktop and the mobile app cannot
/// offer different sets. Each name is written in its own language, as a language picker shows it.
/// </summary>
public static class Languages
{
    public static IReadOnlyList<LanguageOption> Supported { get; } =
        [new("English", "en"), new("Deutsch", "de"), new("Italiano", "it"), new("Español", "es")];

    /// <summary>The supported language matching a culture's two-letter code, or English.</summary>
    public static LanguageOption ForCode(string? code) =>
        Supported.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? Supported[0];
}

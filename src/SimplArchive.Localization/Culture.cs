using System.Globalization;

namespace SimplArchive.Localization;

// Applies the chosen UI language (ADR "Web UI localization — shared resources"; originally ADR 0468): sets the
// current UI culture so the resx accessor resolves to that language. Called before the UI is built — the
// language is chosen at the desktop logon window / the web app startup, so no live switch is needed. Only the
// *UI* culture is set (resource lookup); number/date formatting is left to the OS.
public static class Culture
{
    public static void Apply(string? code)
    {
        var culture = string.IsNullOrWhiteSpace(code)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(code);
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }
}

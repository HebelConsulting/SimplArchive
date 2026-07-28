using System.Globalization;
using System.Resources;

namespace SimplArchive.Localization;

// The localized-strings accessor (ADR "Web UI localization — shared resources"; originally ADR 0468). Reads
// from the embedded Strings.resx (English / invariant) and the de/it/es satellites, keyed by the current UI
// culture. A missing key falls back to the key itself, so a not-yet-translated string is visible (as its key)
// rather than blank. Shared by the Avalonia desktop and the Blazor WASM web client.
public static class Strings
{
    private static readonly ResourceManager Manager =
        new("SimplArchive.Localization.Strings", typeof(Strings).Assembly);

    public static string Get(string key) => Get(key, CultureInfo.CurrentUICulture);

    // An explicit-culture overload so a test can resolve a language without mutating global culture state.
    public static string Get(string key, CultureInfo culture) => Manager.GetString(key, culture) ?? key;

    // Every resource key (from the invariant/English set) — lets a test assert full translation coverage.
    public static IEnumerable<string> AllKeys()
    {
        var set = Manager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
        if (set is null)
        {
            yield break;
        }

        foreach (System.Collections.DictionaryEntry entry in set)
        {
            yield return (string)entry.Key;
        }
    }
}

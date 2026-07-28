using System.Globalization;
using SimplArchive.Localization;

namespace SimplArchive.UiEndToEndTests;

// The i18n framework (ADR "Desktop UI localization"): strings resolve from the English resx by default and the
// German satellite under de, and a missing key falls back to the key itself.
// In the "DesktopConfig" collection so it's serialized with the other tests that mutate process-global state:
// Culture.Apply here sets the process-wide DefaultThreadCurrentUICulture, which would otherwise race with (and
// leak German into) the culture-dependent status messages those tests assert on in English.
[Collection("DesktopConfig")]
public class DesktopLocalizationTests
{
    [Fact]
    public void Strings_resolve_per_culture()
    {
        var en = CultureInfo.GetCultureInfo("en");
        var de = CultureInfo.GetCultureInfo("de");
        var it = CultureInfo.GetCultureInfo("it");
        var es = CultureInfo.GetCultureInfo("es");

        Assert.Equal("New folder", Strings.Get("NewFolder", en));
        Assert.Equal("Neuer Ordner", Strings.Get("NewFolder", de));
        Assert.Equal("Nuova cartella", Strings.Get("NewFolder", it));
        Assert.Equal("Nueva carpeta", Strings.Get("NewFolder", es));

        Assert.Equal("Repositories", Strings.Get("TabRepositories", en));
        Assert.Equal("Archive", Strings.Get("TabRepositories", de));
        Assert.Equal("Archivi", Strings.Get("TabRepositories", it));
        Assert.Equal("Archivos", Strings.Get("TabRepositories", es));

        Assert.Equal("Cancel", Strings.Get("Cancel", en));
        Assert.Equal("Abbrechen", Strings.Get("Cancel", de));
        Assert.Equal("Annulla", Strings.Get("Cancel", it));
        Assert.Equal("Cancelar", Strings.Get("Cancel", es));

        // Every English key has an Italian and a Spanish translation (no fallback-to-key), so the first slice is
        // fully translated in all four languages.
        foreach (var key in Strings.AllKeys())
        {
            Assert.NotEqual(key, Strings.Get(key, it));
            Assert.NotEqual(key, Strings.Get(key, es));
        }

        // A missing key falls back to the key itself (so an untranslated string is visible, not blank).
        Assert.Equal("NoSuchKey", Strings.Get("NoSuchKey", en));
    }

    [Fact]
    public void Apply_sets_the_current_ui_culture()
    {
        var original = CultureInfo.CurrentUICulture;
        var originalDefault = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            Culture.Apply("de");
            Assert.Equal("de", CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
            Assert.Equal("Neuer Ordner", Strings.Get("NewFolder"));
        }
        finally
        {
            // Restore BOTH the thread culture and the process-global default Culture.Apply sets, so this test
            // leaves no ambient culture behind for other tests.
            CultureInfo.CurrentUICulture = original;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefault;
        }
    }
}

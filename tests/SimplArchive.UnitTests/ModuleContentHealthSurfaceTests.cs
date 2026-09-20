using SimplArchive.Localization;

namespace SimplArchive.UnitTests;

// The content-health line exists on BOTH clients and says the same thing (ADR 0511, ADR 0811).
//
// A health line is exactly the kind of surface that ships on one client and is forgotten on the other: it is
// small, it renders only when something is wrong, and a screenshot of the healthy state looks identical
// either way. So a tenant administrator using the desktop could be the one who never learns that a feed has
// stopped — which is the defect this whole feature exists to end, reintroduced by omission on one surface.
public class ModuleContentHealthSurfaceTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(RepoRoot(), relative));

    [Theory]
    [InlineData("src/SimplArchive.Client/Components/Tabs/TenantTab.razor")]
    [InlineData("src/SimplArchive.DesktopClient/Views/TenantSettingsPane.axaml")]
    public void Both_clients_render_the_content_health_line(string file)
    {
        var text = Read(file);

        Assert.True(
            text.Contains("ModContentFailing", StringComparison.Ordinal)
            || text.Contains("ContentHealthText", StringComparison.Ordinal),
            $"{file}: no module content-health line. Both clients must show it — the desktop is the reference "
            + "(ADR 0511) and a line that exists on one surface only means whichever administrator uses the "
            + "other never learns a content feed has died.");
    }

    // NOTE on what is deliberately NOT tested here. An earlier version of this file asserted the two keys
    // resolve in all four languages — and it was VACUOUS: ResourceManager falls back to the neutral English
    // resource when a culture is missing a key, so it returned a perfectly good English string and the
    // assertion passed with the Italian entry deleted. Measured, not assumed.
    //
    // `LocalizationKeyTests.Every_translation_defines_every_key` reads the .resx files themselves and DOES
    // fail on that deletion, so completeness is already guarded one layer down. Two guards where one works
    // would be fine; a second guard that cannot fail is worse than none, because it reads as coverage.
}

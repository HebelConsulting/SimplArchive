using Avalonia.Media;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The environment banner (#501): a server profile declares which environment it IS, and the main window wears
// a thin strip saying so. VM-level — the strip's rendering is covered by `--screenshot --envbanner <id>`.
//
// In the DesktopConfig collection because two of these tests set ServerProfileStore.PathOverride, which is
// STATIC — every class touching it must serialize behind the same collection, or they clobber each other's
// store mid-test. Forgetting this attribute is invisible in isolation runs and failed 2/222 in the full suite,
// with the other class's profiles appearing in this one's assertions.
[Collection("DesktopConfig")]
public class DesktopEnvironmentBannerTests
{
    [Fact]
    public void Production_shows_a_red_banner_with_the_localized_name()
    {
        var banner = new EnvironmentBannerViewModel();
        banner.Set("production");

        Assert.True(banner.IsShown);
        Assert.Equal(SimplArchive.Localization.Strings.Get("EnvProduction"), banner.Name);
        Assert.Equal(Color.Parse("#B91C1C"), Assert.IsType<SolidColorBrush>(banner.Background).Color);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("staging")] // unknown ids mean "no banner", never an error — same posture as a missing theme
    public void Empty_and_unknown_environments_show_nothing(string? id)
    {
        var banner = new EnvironmentBannerViewModel();
        banner.Set("production"); // prove Set(null/unknown) CLEARS, not merely never-shows
        banner.Set(id);

        Assert.False(banner.IsShown);
    }

    [Fact]
    public void The_environment_survives_a_save_and_reload()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"envtest-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        try
        {
            ServerProfileStore.Save(new ServerConfig
            {
                Servers = [new ServerProfile { Name = "Prod", ApiRootUrl = "https://prod.example", Environment = "production" }],
            });

            Assert.Equal("production", ServerProfileStore.Load().Servers.Single().Environment);
        }
        finally
        {
            ServerProfileStore.PathOverride = null;
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Saving_one_profile_does_not_wipe_the_others_theme_or_environment()
    {
        // The regression this feature flushed out: the manager's ctor copied only Name and ApiRootUrl while
        // Persist() wrote Theme back from those copies — so saving ANY edit silently erased the theme of every
        // profile not edited in that session. The ctor copy and Persist() must agree on the field list.
        var tmp = Path.Combine(Path.GetTempPath(), $"envtest-{Guid.NewGuid():N}.json");
        ServerProfileStore.PathOverride = tmp;
        try
        {
            ServerProfileStore.Save(new ServerConfig
            {
                Servers =
                [
                    new ServerProfile { Name = "Prod", ApiRootUrl = "https://prod.example", Theme = "production", Environment = "production" },
                    new ServerProfile { Name = "Dev", ApiRootUrl = "https://dev.example", Theme = "development", Environment = "development" },
                ],
            });

            var vm = new ServerManagerViewModel();
            vm.Selected = vm.Servers.First(s => s.Name == "Dev");
            vm.EditCommand.Execute(null);
            vm.EditName = "Dev renamed";
            vm.SaveCommand.Execute(null);

            var untouched = ServerProfileStore.Load().Servers.Single(s => s.Name == "Prod");
            Assert.Equal("production", untouched.Theme);
            Assert.Equal("production", untouched.Environment);
        }
        finally
        {
            ServerProfileStore.PathOverride = null;
            File.Delete(tmp);
        }
    }
}

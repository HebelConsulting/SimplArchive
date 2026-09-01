using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// A build reports a version it actually has, and an unstamped one says so.
//
// WHY: with no <Version> in the csproj, .NET defaults to 1.0.0. The About box showed that as though it were a
// release — a version this project has never published — and the same value feeds ClientUpdate.RunningVersion,
// so the comparison was worse than cosmetic: 1.0.0 sorts ABOVE every real release, so a development build
// concluded it was UP TO DATE and would never have offered an update. Reported from the running client.
//
// The packaging scripts pass -p:Version from the release tag, so a shipped build reports its real version; this
// is about what an UNSTAMPED build claims.
public class DesktopVersionHonestyTests
{
    [Fact]
    public void An_unstamped_build_does_not_claim_to_be_1_0_0()
    {
        Assert.NotEqual("1.0.0", ClientUpdate.RunningVersion);
    }

    [Fact]
    public void The_dev_version_is_below_every_release_so_an_update_is_offered()
    {
        // The point of the sentinel: a dev build must look OUT OF DATE, never current.
        Assert.Equal(ClientUpdateKind.UpdateAvailable, ClientUpdate.Compare(ClientUpdate.DevVersion, "0.12.0"));
        Assert.Equal(ClientUpdateKind.UpdateAvailable, ClientUpdate.Compare(ClientUpdate.DevVersion, "0.1.0"));

        // …which is exactly what 1.0.0 did NOT do, and is the defect this guards.
        Assert.Equal(ClientUpdateKind.UpToDate, ClientUpdate.Compare("1.0.0", "0.12.0"));
    }

    [Fact]
    public void A_stamped_build_still_compares_normally()
    {
        Assert.Equal(ClientUpdateKind.UpdateAvailable, ClientUpdate.Compare("0.11.0", "0.12.0"));
        Assert.Equal(ClientUpdateKind.UpToDate, ClientUpdate.Compare("0.12.0", "0.12.0"));
    }
}

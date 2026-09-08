using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// What the desktop does when the session bootstrap FAILS after a successful sign-in (#1077).
//
// The bug this pins was invisible in exactly the way that matters: the browser login succeeded, the workbench
// opened, and it silently sat at its default "Not logged in." — no error, no modal, nothing in the UI at all.
// Two defects compounded, and each gets its own test here because either alone would have hidden the other:
//
//   1. A non-2xx GET /api came back as an EMPTY link set, so the next call blamed the CONTRACT ("the API root
//      does not advertise the 'repositories' rel") for what was a 500. The message sent the reader to look at
//      hypermedia while the server was broken.
//   2. The bootstrap was fired and forgotten, so the exception reached nothing but the finalizer's
//      unobserved-task log.
//
// The non-success branch is driven over the real HTTP stack by a base URL that is NOT the API — the server
// manager lets a user type any address, so this is the misconfiguration they actually meet — and the swallow
// by a token the server refuses. No seam was invented for either.
[Collection(UiCollection.Name)]
public class DesktopSessionBootstrapFailureTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopSessionBootstrapFailureTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task A_failed_root_read_reports_the_status_and_never_claims_a_missing_rel()
    {
        // A server address that answers but is not a SimplArchive API — what a mistyped server entry does.
        // Two things this test taught, both worth keeping: the API root is ANONYMOUS (so a refused token
        // cannot produce this — the first draft passed against the broken code for that reason), and the SPA
        // host answers an unknown path with 200 + index.html rather than a 404. So the honest complaint is
        // about the ADDRESS, and it must never be a JsonReaderException from byte 0 of some HTML.
        DesktopClientOptions.ApiBaseUrl = $"{_app.BaseUrl.TrimEnd('/')}/not-the-api/";
        try
        {
            var misdirected = new SimplArchiveApiClient("not-a-valid-token");

            var failure = await Assert.ThrowsAsync<HttpRequestException>(() => misdirected.GetRootLinksAsync());

            Assert.Contains("API root", failure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("advertise", failure.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DesktopClientOptions.ApiBaseUrl = _app.BaseUrl; // process-global: never leave it pointing at the decoy
        }
    }

    [Fact]
    public async Task A_rel_that_truly_is_not_advertised_still_says_so()
    {
        // The other half of the same rule: the honest contract complaint must survive the fix, or the next
        // reader gets a transport story for what genuinely IS a missing rel.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var api = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.Core.RootHrefAsync("no-such-rel-exists"));

        Assert.Contains("advertise", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-such-rel-exists", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bootstrap_that_fails_tells_the_user_instead_of_going_quiet()
    {
        // The heart of #1077: InitializeSessionAsync is fired and forgotten by App, so it must swallow NOTHING
        // silently — it either works or it says so. Before the fix this method threw into a void and the
        // workbench kept its default status, which is the screen the owner reported.
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var refused = new SimplArchiveApiClient("not-a-valid-token");
        var vm = new MainWindowViewModel();
        var defaultStatus = vm.Status;

        await vm.InitializeSessionAsync(refused, "someone@example.test");

        Assert.True(vm.StatusIsError, $"the failure must reach the status line; it read '{vm.Status}'");
        Assert.NotEqual(defaultStatus, vm.Status);
    }
}

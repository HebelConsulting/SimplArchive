using System.Net;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// A credential carried in a module's path (ABI 1.11, ADR 0909) appears NOWHERE in the app's real output: the real
/// process, every category at Verbose, every property printed as JSON. Not a test of the masking code: the
/// assertion is on what the app actually wrote (ADR 0886's rule for a secret on argv, applied to a path).
/// </summary>
/// <remarks>
/// Its own home in the module collection because only that app has a module claiming a path-credential prefix, and
/// a browser-free test because nothing here is a page. The host captures the raw path before any middleware runs:
/// its "Request starting" line and the RequestPath scope on every line of the request, which is what this is for.
/// </remarks>
[Collection(ModuleUiCollection.Name)]
[Trait("Area", "ui-1")]
public class PathCredentialLogTests(ModuleAppFixture app)
{
    // The TestModule's one accepted secret (TestCredentialAuthenticator.Secret). This project references only the
    // ABI, not the module, so the value is written here.
    private const string Secret = "open-sesame";

    [Fact]
    public async Task A_credential_in_the_path_is_written_nowhere_in_the_apps_output()
    {
        var (http, tenantId, _) = await app.ActivateTestModuleAsync();
        using var client = http;
        var credential = ModuleCredentialFormat.Compose(tenantId, Secret);
        var marker = $"index-{Guid.NewGuid():N}.json";
        using var wire = new HttpClient { BaseAddress = new Uri(app.BaseUrl) };

        var accepted = await wire.GetAsync($"/fdroid/{credential}/test-repo/repo/{marker}");
        var refused = await wire.GetAsync($"/fdroid/{ModuleCredentialFormat.Compose(tenantId, "not-the-secret")}/test-repo/repo/{marker}");

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // The summary line of the accepted request is the sign that its output has been written; then the whole log.
        var log = await LogShowingAsync($"/fdroid/***/test-repo/repo/{marker}");
        Assert.Contains("Request starting", log, StringComparison.Ordinal);   // the host's raw-path line WAS written
        Assert.DoesNotContain(Secret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, log, StringComparison.Ordinal);
        Assert.DoesNotContain("not-the-secret", log, StringComparison.Ordinal);
    }

    private async Task<string> LogShowingAsync(string expected)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var log = app.ApiLog();
            if (log.Contains(expected, StringComparison.Ordinal))
            {
                return log;
            }

            await Task.Delay(200);
        }

        throw new Xunit.Sdk.XunitException($"The app never logged '{expected}'. Its output ends:\n{app.ApiLog()[^Math.Min(4000, app.ApiLog().Length)..]}");
    }
}

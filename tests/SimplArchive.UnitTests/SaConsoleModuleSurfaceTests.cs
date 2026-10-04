using System.Net;
using System.Text;
using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.UnitTests;

/// <summary>
/// saconsole's module commands reach the collection from the ROOT, so a service account administering modules can
/// (ADR 0870).
/// </summary>
/// <remarks>
/// They walked root → tenant settings → modules, and tenant settings refuses anyone but a tenant administrator. A
/// service account holding <c>CanManageModules</c> therefore got 403 on every module command — found on the kiosk's
/// first unattended crypto seed, after the API side of ADR 0870 had shipped and its own E2E tests had passed, because
/// those drove the API directly and never this client. The fake server below answers as the real one does for that
/// caller: the root advertises both rels, tenant settings refuses.
/// </remarks>
public sealed class SaConsoleModuleSurfaceTests
{
    private sealed class ServiceAccountView : HttpMessageHandler
    {
        public readonly List<string> Asked = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Asked.Add(path);
            return Task.FromResult(path switch
            {
                "/api" => Json("""{"links":[{"rel":"tenantSettings","href":"/api/tenant-settings","method":"GET"},{"rel":"modules","href":"/api/modules","method":"GET"}]}"""),
                "/api/tenant-settings" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                "/api/modules" => Json("""{"items":[{"moduleId":"encryption","active":false,"links":[]}]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task The_module_listing_is_reached_from_the_root_without_touching_tenant_settings()
    {
        var server = new ServiceAccountView();
        using var http = new HttpClient(server) { BaseAddress = new Uri("https://archive.example.test/") };

        var listing = await ModuleSurface.ListingAsync(new SimplArchiveApi(http), CancellationToken.None);

        Assert.Equal("encryption", Assert.Single(ModuleSurface.Rows(listing)).GetProperty("moduleId").GetString());
        Assert.DoesNotContain("/api/tenant-settings", server.Asked);
    }
}

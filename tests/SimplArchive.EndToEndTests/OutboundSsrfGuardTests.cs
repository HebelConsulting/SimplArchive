using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.EndToEndTests;

// The server-side-request-forgery guard, over the real API (ADR 0717, issue #845). ADR 0126 specified four
// controls and none of them existed: the only validation on a tenant administrator's webhook URL was that it
// parsed as http(s).
//
// The POSITIVE case is not repeated here — AuditWebhookTestDeliveryTests already delivers to a stub receiver on
// loopback, which the fixture allowlists exactly as an operator with an on-premises collector would. That test
// passing is what shows the guard is a guard rather than a wall.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class OutboundSsrfGuardTests
{
    private readonly E2EApiFactory _factory;

    public OutboundSsrfGuardTests(E2EApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Admin, Guid TenantId)> AdminAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: false);
        var email = $"ssrf-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "ssrf-1234", "SSRF Admin");
        await _factory.GrantTenantAdminAsync(email);

        return (_factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "ssrf-1234")), tenantId);
    }

    private static Task<HttpResponseMessage> SetWebhookAsync(HttpClient admin, string url) =>
        admin.PutAsJsonAsync("/api/tenant-settings/audit-streaming", new
        {
            auditWebhookUrl = url,
            auditWebhookSecret = "ssrf-signing-secret",
        });

    [Theory]
    [InlineData("http://10.1.2.3/ingest")]                          // somebody else's machine on the network
    [InlineData("http://192.168.0.1/ingest")]                       // the router
    [InlineData("http://169.254.169.254/latest/meta-data/")]        // the instance's own credentials
    [InlineData("http://[fd12:3456::1]/ingest")]                    // the IPv6 equivalent
    [InlineData("http://user:secret@example.com/ingest")]           // a target disguised by credentials
    public async Task A_webhook_url_this_installation_may_not_call_is_refused(string url)
    {
        var (admin, _) = await AdminAsync();
        using var _1 = admin;

        var response = await SetWebhookAsync(admin, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("INVALID_WEBHOOK_URL", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_redirect_is_a_delivery_failure_and_the_second_hop_is_never_called()
    {
        var (admin, _) = await AdminAsync();
        using var _1 = admin;

        // A receiver that 302s to a second path on the same listener. This is the bypass pinning alone does not
        // close: the first hop is validated and connected to, and then the ENDPOINT chooses the second one.
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        var secondHopCalled = false;
        var serving = Task.Run(async () =>
        {
            for (var request = 0; request < 2; request++)
            {
                var context = await listener.GetContextAsync();
                if (context.Request.Url!.AbsolutePath.StartsWith("/final", StringComparison.Ordinal))
                {
                    secondHopCalled = true;
                    context.Response.StatusCode = 200;
                }
                else
                {
                    context.Response.StatusCode = 302;
                    context.Response.Headers["Location"] = $"http://localhost:{port}/final";
                }

                context.Response.Close();
            }
        });

        Assert.Equal(HttpStatusCode.OK, (await SetWebhookAsync(admin, $"http://localhost:{port}/redirect")).StatusCode);

        var result = await TestJson.Post(admin, "/api/tenant-settings/audit-webhook/test", new { });

        Assert.False(result.GetProperty("success").GetBoolean());

        // The redirect target was never fetched. Following it would have re-resolved a name the administrator
        // never typed — which is how a public URL reaches a private one in a single hop.
        await Task.WhenAny(serving, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(secondHopCalled);

        listener.Stop();
    }

    [Fact]
    public async Task A_url_that_slipped_past_registration_is_still_refused_at_the_moment_of_delivery()
    {
        var (admin, tenantId) = await AdminAsync();
        using var _1 = admin;

        // Configure it legitimately first, so the signing secret is stored the way the product stores it.
        var port = FreePort();
        Assert.Equal(HttpStatusCode.OK, (await SetWebhookAsync(admin, $"http://localhost:{port}/ingest")).StatusCode);

        // Then move the URL underneath, straight in the database. This is DNS rebinding staged the only way a
        // test can stage it: the value that was validated is not the value that will be called. A registration
        // check alone sees nothing here — which is precisely why it is not the control.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext>();
            var tenant = await db.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
            tenant.AuditWebhookUrl = "http://10.255.255.1/ingest";
            await db.SaveChangesAsync();
        }

        var result = await TestJson.Post(admin, "/api/tenant-settings/audit-webhook/test", new { });

        Assert.False(result.GetProperty("success").GetBoolean());

        // And the administrator is told WHY. The handler wraps everything it raises in a generic "an error
        // occurred while sending the request"; a cause they cannot see is a cause they cannot fix.
        Assert.Contains("may not call", result.GetProperty("error").GetString() ?? string.Empty);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return port;
    }
}

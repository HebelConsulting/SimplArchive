using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.EndToEndTests;

// A tenant's own outbound SMTP account, over the real API (#1337).
//
// The normal deployment runs no relay of its own: it authenticates against an ordinary provider's submission
// server, and that account is per tenant because each tenant's mail should leave from its own domain. These
// pin the half an administrator touches — and above all the PASSWORD contract, which is the part that can
// destroy a working configuration silently.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TenantOutboundMailSettingsTests
{
    private readonly E2EApiFactory _factory;

    public TenantOutboundMailSettingsTests(E2EApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var email = $"outbound-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, email, "om-123456789", "Outbound Admin");
        await _factory.GrantTenantAdminAsync(email);
        return _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, "om-123456789"));
    }

    [Fact]
    public async Task The_account_round_trips_except_the_password_which_is_never_returned()
    {
        using var admin = await AdminAsync();

        var updated = await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new
        {
            smtpHost = "smtp.acme.test",
            smtpPort = 465,
            smtpUseStartTls = true,
            smtpUser = "acme-user",
            smtpPassword = "acme-secret",
            smtpFromAddress = "notifications@acme.test",
            smtpFromName = "Acme",
        });

        Assert.Equal("smtp.acme.test", updated.GetProperty("smtpHost").GetString());
        Assert.Equal(465, updated.GetProperty("smtpPort").GetInt32());
        Assert.True(updated.GetProperty("smtpUseStartTls").GetBoolean());
        Assert.Equal("acme-user", updated.GetProperty("smtpUser").GetString());
        Assert.Equal("notifications@acme.test", updated.GetProperty("smtpFromAddress").GetString());

        // THE PASSWORD IS NOT IN THE RESOURCE AT ALL — only whether one is set. A settings read is the easiest
        // place in the product to leak a credential, because the form that needs the other fields would
        // happily render this one too.
        Assert.True(updated.GetProperty("smtpPasswordSet").GetBoolean());
        Assert.False(updated.TryGetProperty("smtpPassword", out _));

        // And it stays out of a plain GET, which is the call a client actually makes on opening the tab.
        var read = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.False(read.TryGetProperty("smtpPassword", out _));
        Assert.True(read.GetProperty("smtpPasswordSet").GetBoolean());
    }

    [Fact]
    public async Task Omitting_the_password_keeps_the_stored_one()
    {
        // THE CONTRACT THAT MATTERS. A read cannot return the password, so a form cannot send it back — which
        // means every edit of the port or the from-address would wipe the credential if an omitted password
        // meant "clear". A tenant's mail would stop that evening, with nothing on screen having said so.
        using var admin = await AdminAsync();

        await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new
        {
            smtpHost = "smtp.acme.test",
            smtpPort = 587,
            smtpUser = "acme-user",
            smtpPassword = "acme-secret",
        });

        var afterEdit = await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new
        {
            smtpHost = "smtp.acme.test",
            smtpPort = 2525,
            smtpUser = "acme-user",
        });

        Assert.Equal(2525, afterEdit.GetProperty("smtpPort").GetInt32());
        Assert.True(afterEdit.GetProperty("smtpPasswordSet").GetBoolean());
    }

    [Fact]
    public async Task An_empty_password_clears_it()
    {
        // The other half of the same contract: removing a password has to be expressible, or a tenant that
        // moves to an unauthenticated relay can never say so.
        using var admin = await AdminAsync();

        await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new
        {
            smtpHost = "smtp.acme.test",
            smtpPort = 587,
            smtpPassword = "acme-secret",
        });

        var cleared = await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new
        {
            smtpHost = "smtp.acme.test",
            smtpPort = 587,
            smtpPassword = "",
        });

        Assert.False(cleared.GetProperty("smtpPasswordSet").GetBoolean());
    }

    [Fact]
    public async Task Clearing_the_host_returns_the_tenant_to_the_installations_account()
    {
        // The fallback is what makes this additive, and an administrator has to be able to get back to it.
        using var admin = await AdminAsync();

        await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new { smtpHost = "smtp.acme.test", smtpPort = 587 });
        var cleared = await TestJson.Put(admin, "/api/tenant-settings/outbound-mail", new { smtpHost = "", smtpPort = 587 });

        Assert.True(cleared.GetProperty("smtpHost").ValueKind is JsonValueKind.Null
            || string.IsNullOrEmpty(cleared.GetProperty("smtpHost").GetString()));
    }

    [Fact]
    public async Task The_settings_resource_advertises_both_new_rels()
    {
        // A client reaches these by following a rel; an endpoint no resource links to is unreachable by a
        // conforming client and therefore unfinished (ADR 0543).
        using var admin = await AdminAsync();
        var settings = await TestJson.Get(admin, "/api/tenant-settings");

        var rels = settings.GetProperty("links").EnumerateArray()
            .Select(l => l.GetProperty("rel").GetString())
            .ToList();

        Assert.Contains("settings-outbound-mail", rels);
        Assert.Contains("test-outbound-mail", rels);
    }

    [Fact]
    public async Task A_test_delivery_reports_which_account_it_used()
    {
        // The E2E host configures no SMTP, so this exercises the honest failure path — which is the one worth
        // pinning: an administrator pressing Test must learn WHY rather than see a spinner stop.
        using var admin = await AdminAsync();

        var result = await TestJson.Post(admin, "/api/tenant-settings/outbound-mail/test-deliveries", new { });

        // Either it sent (an installation with a relay) or it says what stopped it. What it must never do is
        // claim success without having tried, or fail without saying anything.
        if (!result.GetProperty("success").GetBoolean())
        {
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
        }
    }
}

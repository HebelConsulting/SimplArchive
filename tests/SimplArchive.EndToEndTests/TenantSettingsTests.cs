using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the tenant-admin settings surface (ADR "Tenant-admin
// settings tab"): a tenant admin reads + updates the editable Tenant columns; a non-admin is refused; invalid
// OCR languages are rejected.
[Collection(E2ECollection.Name)]
public class TenantSettingsTests
{
    private readonly E2EApiFactory _factory;

    public TenantSettingsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_admin_reads_and_updates_settings_others_are_refused()
    {
        var (_, _, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);

        var adminEmail = $"ts-admin-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, adminEmail, "ts-1234", "Tenant Admin");
        await _factory.GrantTenantAdminAsync(adminEmail);
        using var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(adminEmail, "ts-1234"));

        // A plain (non-admin) user in the same tenant.
        var plainEmail = $"ts-plain-{Guid.NewGuid():N}@e2e.local";
        await _factory.SeedUserAsync(tenantId, plainEmail, "ts-1234", "Plain User");
        using var plain = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(plainEmail, "ts-1234"));

        // A non-admin can't read or write the settings.
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/tenant-settings")).StatusCode);

        // The admin reads the current settings (seeded defaults).
        var settings = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.Equal(tenantId, settings.GetProperty("id").GetGuid());
        Assert.Equal("Active", settings.GetProperty("status").GetString());
        Assert.Equal(365, settings.GetProperty("auditRetentionDays").GetInt32());
        Assert.Equal(0, settings.GetProperty("checkoutTtlDays").GetInt32()); // disabled by default
        Assert.Equal(0, settings.GetProperty("wormLockMode").GetInt32()); // Governance by default
        Assert.False(settings.GetProperty("requireMfa").GetBoolean()); // opt-in, off by default

        // External links (ADR 0546): OFF by default, with the caps present so the tenant UI has something to
        // render the moment it is switched on. The default matters — an existing tenant must not gain an
        // unauthenticated way for documents to leave it as a side effect of a migration.
        Assert.False(settings.GetProperty("allowExternalLinks").GetBoolean());
        Assert.Equal(180, settings.GetProperty("externalLinkMaxDays").GetInt32());
        Assert.Equal(5, settings.GetProperty("externalLinkDefaultAccesses").GetInt32());
        Assert.False(string.IsNullOrEmpty(settings.GetProperty("defaultOcrLanguages").GetString()));

        // Update name + OCR languages + retention + check-out TTL + WORM mode (1 = Compliance) + require-MFA.
        var newName = $"Renamed {Guid.NewGuid():N}";
        var updated = await TestJson.Put(admin, "/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng+spa", auditRetentionDays = 90, checkoutTtlDays = 14, wormLockMode = 1, requireMfa = true });
        Assert.Equal(newName, updated.GetProperty("name").GetString());
        Assert.Equal("eng+spa", updated.GetProperty("defaultOcrLanguages").GetString());

        // The external-link switch and its caps round-trip through the same PUT, so an administrator can actually
        // turn the feature on — without this the flags default to false and nothing in the product can set them.
        var shared = await TestJson.Put(admin, "/api/tenant-settings", new
        {
            name = newName,
            defaultOcrLanguages = "eng+spa",
            auditRetentionDays = 90,
            checkoutTtlDays = 14,
            // Carried through deliberately: PUT is a FULL replacement (ADR 0003 prefers it over PATCH), so
            // omitting these would reset them and break the assertions that follow.
            wormLockMode = 1,
            requireMfa = true,
            allowExternalLinks = true,
            externalLinkMaxDays = 30,
            externalLinkDefaultAccesses = 2,
        });
        Assert.True(shared.GetProperty("allowExternalLinks").GetBoolean());
        Assert.Equal(30, shared.GetProperty("externalLinkMaxDays").GetInt32());
        Assert.Equal(2, shared.GetProperty("externalLinkDefaultAccesses").GetInt32());
        Assert.Equal(90, updated.GetProperty("auditRetentionDays").GetInt32());
        Assert.Equal(14, updated.GetProperty("checkoutTtlDays").GetInt32());
        Assert.Equal(1, updated.GetProperty("wormLockMode").GetInt32());
        Assert.True(updated.GetProperty("requireMfa").GetBoolean());

        // The change persisted.
        var reread = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.Equal(newName, reread.GetProperty("name").GetString());
        Assert.Equal(14, reread.GetProperty("checkoutTtlDays").GetInt32());
        Assert.Equal(1, reread.GetProperty("wormLockMode").GetInt32());
        Assert.True(reread.GetProperty("requireMfa").GetBoolean());

        // Turn require-MFA back off so it doesn't affect the shared demo tenant used elsewhere.
        await TestJson.Put(admin, "/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng+spa", auditRetentionDays = 90, checkoutTtlDays = 14, wormLockMode = 1, requireMfa = false });

        // An unsupported OCR code is rejected; a negative retention / check-out TTL is rejected.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng+zzz", auditRetentionDays = 90, checkoutTtlDays = 14 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng", auditRetentionDays = -1, checkoutTtlDays = 14 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng", auditRetentionDays = 90, checkoutTtlDays = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings", new { name = newName, defaultOcrLanguages = "eng", auditRetentionDays = 90, checkoutTtlDays = 14, wormLockMode = 99 })).StatusCode);

        // A non-admin can't write.
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync("/api/tenant-settings", new { name = "hijack", defaultOcrLanguages = "eng", auditRetentionDays = 10, checkoutTtlDays = 5 })).StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;

namespace SimplArchive.EndToEndTests;

// End-to-end over the real API + Postgres, exercising the tenant-admin settings surface (ADRs "Tenant-admin
// settings tab" + "Per-group tenant settings", #530 tranche 10): a tenant admin reads the settings and updates
// them GROUP BY GROUP via the settings-<group> sub-resources; a non-admin is refused; invalid values are
// rejected per group. Each group PUT is a full replacement of ITS GROUP only — the other groups keep their
// values, which is the point of the split and is asserted below.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class TenantSettingsTests
{
    private readonly E2EApiFactory _factory;

    public TenantSettingsTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Tenant_admin_reads_and_updates_settings_per_group_others_are_refused()
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

        // The admin reads the current settings (seeded defaults) — and the resource advertises one writable
        // sub-resource per group (ADR 0543: the rels are how a client reaches them).
        var settings = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.Equal(tenantId, settings.GetProperty("id").GetGuid());
        Assert.Equal("Active", settings.GetProperty("status").GetString());
        Assert.Equal(365, settings.GetProperty("auditRetentionDays").GetInt32());
        Assert.Equal(0, settings.GetProperty("checkoutTtlDays").GetInt32()); // disabled by default
        Assert.Equal(0, settings.GetProperty("wormLockMode").GetInt32()); // Governance by default
        Assert.False(settings.GetProperty("requireMfa").GetBoolean()); // opt-in, off by default
        var rels = settings.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("rel").GetString()).ToList();
        foreach (var group in new[] { "general", "capture", "security", "records", "checkout", "storage", "external-links", "audit-streaming" })
        {
            Assert.Contains($"settings-{group}", rels);
        }

        // External links (ADR 0546): OFF by default, with the caps present so the tenant UI has something to
        // render the moment it is switched on. The default matters — an existing tenant must not gain an
        // unauthenticated way for documents to leave it as a side effect of a migration.
        Assert.False(settings.GetProperty("allowExternalLinks").GetBoolean());
        Assert.Equal(180, settings.GetProperty("externalLinkMaxDays").GetInt32());
        Assert.Equal(5, settings.GetProperty("externalLinkDefaultAccesses").GetInt32());
        Assert.False(string.IsNullOrEmpty(settings.GetProperty("defaultOcrLanguages").GetString()));

        // Update group by group: name (general), OCR (capture), retention + WORM (records), TTL (checkout),
        // require-MFA (security). Each PUT touches ONLY its group.
        var newName = $"Renamed {Guid.NewGuid():N}";
        var updated = await TestJson.Put(admin, "/api/tenant-settings/general", new { name = newName });
        Assert.Equal(newName, updated.GetProperty("name").GetString());

        updated = await TestJson.Put(admin, "/api/tenant-settings/capture", new { defaultOcrLanguages = "eng+spa", restrictTagsToCatalog = false });
        Assert.Equal("eng+spa", updated.GetProperty("defaultOcrLanguages").GetString());

        updated = await TestJson.Put(admin, "/api/tenant-settings/records", new { auditRetentionDays = 90, wormLockMode = 1, requireDispositionReview = false });
        Assert.Equal(90, updated.GetProperty("auditRetentionDays").GetInt32());
        Assert.Equal(1, updated.GetProperty("wormLockMode").GetInt32());

        updated = await TestJson.Put(admin, "/api/tenant-settings/checkout", new { checkoutTtlDays = 14, checkoutWarningDays = 1 });
        Assert.Equal(14, updated.GetProperty("checkoutTtlDays").GetInt32());

        updated = await TestJson.Put(admin, "/api/tenant-settings/security", new { requireMfa = true, allowPasskeyLogin = false, enforceClearance = false });
        Assert.True(updated.GetProperty("requireMfa").GetBoolean());
        // A group PUT replaces only ITS group — the name and records values set above survived it.
        Assert.Equal(newName, updated.GetProperty("name").GetString());
        Assert.Equal(90, updated.GetProperty("auditRetentionDays").GetInt32());

        // The external-link switch and its caps live in their own group, so an administrator can turn the
        // feature on without re-stating any other setting.
        var shared = await TestJson.Put(admin, "/api/tenant-settings/external-links", new
        {
            allowExternalLinks = true,
            externalLinkMaxDays = 30,
            externalLinkDefaultAccesses = 2,
            showExternalLinkUrl = false,
        });
        Assert.True(shared.GetProperty("allowExternalLinks").GetBoolean());
        Assert.Equal(30, shared.GetProperty("externalLinkMaxDays").GetInt32());
        Assert.Equal(2, shared.GetProperty("externalLinkDefaultAccesses").GetInt32());

        // The changes persisted.
        var reread = await TestJson.Get(admin, "/api/tenant-settings");
        Assert.Equal(newName, reread.GetProperty("name").GetString());
        Assert.Equal(14, reread.GetProperty("checkoutTtlDays").GetInt32());
        Assert.Equal(1, reread.GetProperty("wormLockMode").GetInt32());
        Assert.True(reread.GetProperty("requireMfa").GetBoolean());

        // Turn require-MFA back off so it doesn't affect the shared demo tenant used elsewhere.
        await TestJson.Put(admin, "/api/tenant-settings/security", new { requireMfa = false, allowPasskeyLogin = false, enforceClearance = false });

        // Per-group validation: an unsupported OCR code, a negative retention / check-out TTL, an unknown
        // WORM mode — each rejected by the group that owns the field.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings/capture", new { defaultOcrLanguages = "eng+zzz", restrictTagsToCatalog = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings/records", new { auditRetentionDays = -1, wormLockMode = 0, requireDispositionReview = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings/checkout", new { checkoutTtlDays = -1, checkoutWarningDays = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings/records", new { auditRetentionDays = 90, wormLockMode = 99, requireDispositionReview = false })).StatusCode);

        // A missing name is rejected; an empty general PUT can't blank the tenant.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/tenant-settings/general", new { name = "" })).StatusCode);

        // A non-admin can't write any group.
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync("/api/tenant-settings/general", new { name = "hijack" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.PutAsJsonAsync("/api/tenant-settings/security", new { requireMfa = false, allowPasskeyLogin = false, enforceClearance = false })).StatusCode);
    }
}

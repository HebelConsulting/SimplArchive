using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// A tenant with a repository, its owner (a service account) and a tenant administrator, and the steps that activate
/// the test module there: file a vendor-signed licence, activate, grant the module's principal.
/// </summary>
/// <remarks>
/// Extracted from <see cref="ModuleControllerTests"/> when a second class needed it (#1634), so there is one copy
/// to fix (ADR 0877). The caller sets <c>SIMPLARCHIVE_TESTMODULE_VERIFY_KEY</c> to the vendor key's public half:
/// the host loads the module in its own load context, so only the environment reaches it.
/// </remarks>
internal static class TestModuleRig
{
    internal sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    internal static async Task<Rig> CreateAsync(E2EApiFactory factory)
    {
        var (clientId, secret, tenantId) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Mods {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"modadmin-{Guid.NewGuid():N}@e2e.local";
        const string password = "modadmin-1234";
        // canManageServiceAccounts on top of tenant-admin: the consent act needs to FIND the module's
        // principal in the service-accounts listing, whose gate is that specific system right.
        var adminId = await factory.SeedUserAsync(tenantId, email, password, "Module Admin", isTenantAdmin: true, canManageServiceAccounts: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });
        var admin = factory.CreateAuthedClient(await factory.GetUserTokenAsync(email, password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    internal static async Task<Guid> FileLicenseAsync(Rig rig, DateOnly supportEnd, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], rig.TenantId, supportEnd, ModuleAbiVersion.Major, string.Empty)
            .Sign(vendorKey);
        var json = JsonSerializer.Serialize(license, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var docId = (await TestJson.Post(rig.Owner, $"/api/documents/{rig.RepoId}/children",
            new { name = $"License {Guid.NewGuid():N}" })).GetProperty("id").GetGuid();
        var created = await TestJson.Post(rig.Owner, $"/api/documents/{docId}/versions", new { fileExtension = ".json" });
        using (var storage = new HttpClient())
        {
            (await storage.PutAsync(created.GetProperty("uploadUrl").GetString()!,
                new ByteArrayContent(Encoding.UTF8.GetBytes(json)))).EnsureSuccessStatusCode();
        }

        await TestJson.Put(rig.Owner, $"/api/documents/{docId}/versions/{created.GetProperty("id").GetGuid()}", new { });
        return docId;
    }

    internal static async Task ActivateAsync(Rig rig, ECDsa vendorKey, bool grantPrincipal = true)
    {
        var licenseDocId = await FileLicenseAsync(rig, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), vendorKey);
        await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = licenseDocId });
        if (grantPrincipal)
        {
            await GrantPrincipalAsync(rig);
        }
    }

    /// <summary>The consent act (ADR 0736): an ordinary ACL grant to the module's own login-less
    /// principal — created by the activation, listed like any service account.</summary>
    internal static async Task GrantPrincipalAsync(Rig rig)
    {
        var principalId = (await TestJson.Get(rig.Admin, "/api/service-accounts"))
            .GetProperty("serviceAccounts").EnumerateArray()
            .Single(sa => sa.GetProperty("name").GetString() == "Module: Test Module")
            .GetProperty("id").GetGuid();
        await TestJson.Put(rig.Admin, $"/api/documents/{rig.RepoId}/acl-entries/service-accounts/{principalId}",
            new { canSee = true });
    }
}

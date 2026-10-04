using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// THE STRICT TENANT'S READERS GET THEIR CERTIFICATES FROM A MODULE (ADR 0890). With the encryption service's registry
// retired, an activated module is the only source a tenant whose mode delivers envelopes has — and every such mode
// refuses to start without one loaded. So the Strict tenant ACTIVATES the in-repo TestModule when it is created,
// and a test files a reader's certificate through the module's own enrolment route: the path a real installation
// uses, rather than a column no installation of this tier can write.
//
// Activated EAGERLY, at tenant creation, and once: several test classes share the one Strict tenant, and a test that
// ran before activation would see "lapsed licence" where its neighbours see "no certificate" — an order dependence,
// not a test result. The certificate folder is the tenant's own, granted to the module principal (a module cannot
// see its own documents without a grant).
public sealed partial class E2EApiFactory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (HttpClient Owner, Guid Folder)> _strictReaders = new();

    /// <summary>Activates the TestModule for a Strict tenant and prepares the folder its readers' certificates live in.</summary>
    private async Task ActivateReaderCertificateModuleAsync(Guid tenantId)
    {
        var (clientId, secret) = await SeedServiceAccountInTenantAsync(tenantId, canManageRepositories: true);
        var owner = CreateAuthedClient(await GetTokenAsync(clientId, secret));

        var folder = (await (await owner.PostAsJsonAsync("/api/repositories",
            new { name = $"Reader certificates {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var licenceDocument = (await (await owner.PostAsJsonAsync($"/api/documents/{folder}/children",
            new { name = "Test module licence" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // A real signed licence, verified by the real service — only the HTTP filing is skipped, because on a
        // Strict tenant that is a client-side-encrypted upload and is not what these tests are about.
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var licenceJson = JsonSerializer.Serialize(
            new TenantLicense(["test-module"], tenantId, DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                ModuleAbiVersion.Major, string.Empty).Sign(vendorKey),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var previousKey = Environment.GetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY");
        Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", vendorKey.ExportSubjectPublicKeyInfoPem());
        Guid principalId;
        try
        {
            using var scope = Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
            var module = scope.ServiceProvider.GetRequiredService<IReadOnlyList<ModuleLoader.LoadedModule>>()
                .Single(m => m.Module.ModuleId == "test-module").Module;
            await scope.ServiceProvider.GetRequiredService<ModuleActivationService>()
                .ActivateAsync(module, licenceJson, licenceDocument, tenantId, actorUserId: null);

            principalId = await scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>().ServiceAccounts
                .Where(s => s.OpenIddictApplicationClientId == ModulePrincipal.ClientIdFor("test-module"))
                .Select(s => s.Id)
                .SingleAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY", previousKey);
        }

        // The consent act: the module principal may see and file in its certificate folder, and nothing else.
        (await owner.PutAsJsonAsync($"/api/documents/{folder}/acl-entries/service-accounts/{principalId}",
            new { canSee = true, canReadContent = true, canEditContent = true, canEditIndexData = true, canCreateSubItems = true }))
            .EnsureSuccessStatusCode();

        _strictReaders[tenantId] = (owner, folder);
    }

    /// <summary>
    /// Gives a reader <paramref name="pem"/> the way THEIR tenant gets certificates: through the module where it is
    /// active (the Strict tenant), else on the reader's own column — the source a tenant whose mode does not
    /// envelope keeps (ADR 0890's narrowing). The one place tests do this, so the two cannot drift apart.
    /// </summary>
    public async Task EnrolReaderCertificateAsync(Guid tenantId, Guid userId, string pem)
    {
        if (_strictReaders.TryGetValue(tenantId, out var module))
        {
            (await module.Owner.PostAsJsonAsync($"/api/test-module/reader-certificates?parentId={module.Folder}",
                new { userId, certificatePem = pem })).EnsureSuccessStatusCode();
            return;
        }

        await SetColumnCertificateAsync(userId, pem);
    }

    /// <summary>Withdraws a reader's certificates — "lost their certificate" — from wherever their tenant keeps them.</summary>
    public async Task WithdrawReaderCertificatesAsync(Guid tenantId, Guid userId)
    {
        if (_strictReaders.TryGetValue(tenantId, out var module))
        {
            (await module.Owner.PutAsync($"/api/test-module/reader-certificates/withdrawn?userId={userId}", content: null))
                .EnsureSuccessStatusCode();
            return;
        }

        await SetColumnCertificateAsync(userId, null);
    }

    private async Task SetColumnCertificateAsync(Guid userId, string? pem)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();
        var user = await db.Users.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(u => u.Id == userId);
        user.SmimeCertificatePem = pem;
        await db.SaveChangesAsync();
    }
}

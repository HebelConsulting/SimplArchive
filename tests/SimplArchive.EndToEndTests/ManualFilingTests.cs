using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Api.Manuals;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Modules;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.EndToEndTests;

// A module ships its manual and the core files it (ABI 1.5, ADR 0891), against the real loader, Postgres and object
// storage: nothing is filed without a standard repository; once there is one the manual is filed ONCE into the lazily
// created "SimplArchive Manuals" folder (found by its mask), an unchanged package files nothing, a changed one becomes
// a new version of the SAME document, a deleted manual is filed afresh — and two instances running the check at the
// same moment still produce one document and one version. SimplArchive's own manual goes the same way.
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-2")]
public class ManualFilingTests
{
    private const string VerifyKeyVariable = "SIMPLARCHIVE_TESTMODULE_VERIFY_KEY";

    private readonly E2EApiFactory _factory;

    public ManualFilingTests(E2EApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_manual_is_filed_once_versioned_when_the_package_changes_and_refiled_when_deleted()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            // Activation with no location configured files nothing — no default place is guessed.
            await ActivateAsync(rig, vendorKey);
            Assert.Null((await ActivationAsync(rig.TenantId)).ManualDocumentId);

            await MakeStandardAsync(rig.TenantId, rig.RepoId);
            Assert.Equal(ManualFilingOutcome.Filed, await FileAsync(rig.TenantId));

            var activation = await ActivationAsync(rig.TenantId);
            var documentId = Assert.IsType<Guid>(activation.ManualDocumentId);
            var (document, versions) = await DocumentAsync(rig.TenantId, documentId);
            var folder = await ManualsFolderAsync(rig.TenantId);
            Assert.Equal(rig.RepoId, folder.ParentId);
            Assert.Equal("SimplArchive Manuals", folder.Name);
            Assert.Equal(folder.Id, document.ParentId);
            Assert.Equal("test-module", await FieldAsync(rig.TenantId, documentId, "Article number"));
            Assert.Equal("Test Module", await FieldAsync(rig.TenantId, documentId, "Product name"));
            Assert.Equal("SimplArchive", await FieldAsync(rig.TenantId, documentId, "Brand"));
            Assert.Equal("Test Module Manual", document.Name);
            Assert.NotNull(document.CreatedByServiceAccountId);   // the module's own principal, not a person
            Assert.Equal([Hash("1")], versions.Select(v => v.Sha256Hash));

            // The startup path, with the package unchanged: nothing new — the property two instances rely on.
            await StartupAsync(rig.TenantId);
            Assert.Single((await DocumentAsync(rig.TenantId, documentId)).Versions);

            // An upgraded package: a NEW VERSION of the same document, never a second document.
            Environment.SetEnvironmentVariable(TestModule.TestModule.ManualEditionVariable, "2");
            Assert.Equal(ManualFilingOutcome.Versioned, await FileAsync(rig.TenantId));
            Assert.Equal(documentId, (await ActivationAsync(rig.TenantId)).ManualDocumentId);
            Assert.Equal([Hash("1"), Hash("2")], (await DocumentAsync(rig.TenantId, documentId)).Versions.Select(v => v.Sha256Hash));
            Assert.Equal(ManualFilingOutcome.Unchanged, await FileAsync(rig.TenantId));

            // Somebody deleted it: the archive should still carry the manual the module ships, so it is filed afresh.
            await SoftDeleteAsync(rig.TenantId, documentId);
            Assert.Equal(ManualFilingOutcome.Filed, await FileAsync(rig.TenantId));
            var refiled = Assert.IsType<Guid>((await ActivationAsync(rig.TenantId)).ManualDocumentId);
            Assert.NotEqual(documentId, refiled);
            Assert.Equal([Hash("2")], (await DocumentAsync(rig.TenantId, refiled)).Versions.Select(v => v.Sha256Hash));

            await using var scope = await TenantScopeAsync(rig.TenantId);
            // Three for the module's manual (filed, versioned, refiled); the startup pass also filed SimplArchive's own.
            Assert.Equal(3, await scope.Db.AuditEvents.CountAsync(e => e.Action == "Manual.Filed" && e.TargetName == "Test Module Manual"));
            Assert.Equal(1, await scope.Db.AuditEvents.CountAsync(e => e.Action == "Manual.Filed" && e.TargetName == "SimplArchive Manual"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
            Environment.SetEnvironmentVariable(TestModule.TestModule.ManualEditionVariable, null);
        }
    }

    [Fact]
    public async Task Two_instances_checking_at_once_file_one_document_and_one_version()
    {
        var rig = await RigAsync();
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Environment.SetEnvironmentVariable(VerifyKeyVariable, vendorKey.ExportSubjectPublicKeyInfoPem());
        try
        {
            await ActivateAsync(rig, vendorKey);
            await MakeStandardAsync(rig.TenantId, rig.RepoId);

            // Both start together; the activation-row lock makes the second wait, then read what the first committed.
            var first = await Task.WhenAll(FileAsync(rig.TenantId), FileAsync(rig.TenantId));
            Assert.Equal([ManualFilingOutcome.Unchanged, ManualFilingOutcome.Filed], first.Order());

            await using (var scope = await TenantScopeAsync(rig.TenantId))
            {
                Assert.Single(await scope.Db.Documents.Where(d => d.Name == "Test Module Manual").ToListAsync());
                Assert.Single(await scope.Db.Documents.Where(d => d.Name.StartsWith("SimplArchive Manuals")).ToListAsync());
            }

            Environment.SetEnvironmentVariable(TestModule.TestModule.ManualEditionVariable, "3");
            var second = await Task.WhenAll(FileAsync(rig.TenantId), FileAsync(rig.TenantId));
            Assert.Equal([ManualFilingOutcome.Unchanged, ManualFilingOutcome.Versioned], second.Order());

            var documentId = Assert.IsType<Guid>((await ActivationAsync(rig.TenantId)).ManualDocumentId);
            Assert.Equal([Hash("1"), Hash("3")], (await DocumentAsync(rig.TenantId, documentId)).Versions.Select(v => v.Sha256Hash));
        }
        finally
        {
            Environment.SetEnvironmentVariable(VerifyKeyVariable, null);
            Environment.SetEnvironmentVariable(TestModule.TestModule.ManualEditionVariable, null);
        }
    }

    [Fact]
    public async Task SimplArchives_own_manual_is_filed_once_beside_the_modules_and_found_again_by_its_fields()
    {
        var rig = await RigAsync();

        // No standard repository: nothing filed — and nothing guessed.
        Assert.Equal(ManualFilingOutcome.NoLocation, await CoreAsync(rig.TenantId));

        await MakeStandardAsync(rig.TenantId, rig.RepoId);
        Assert.Equal(ManualFilingOutcome.Filed, await CoreAsync(rig.TenantId));
        Assert.Equal(ManualFilingOutcome.Unchanged, await CoreAsync(rig.TenantId));

        var folder = await ManualsFolderAsync(rig.TenantId);
        await using var scope = await TenantScopeAsync(rig.TenantId);
        var manual = await scope.Db.Documents.AsNoTracking().SingleAsync(d => d.ParentId == folder.Id);
        Assert.Equal("SimplArchive Manual", manual.Name);
        Assert.Equal("simplarchive-core", await FieldAsync(rig.TenantId, manual.Id, "Article number"));
    }

    private async Task<ManualFilingOutcome> CoreAsync(Guid tenantId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        return await scope.Services.GetRequiredService<ManualFiler>().FileCoreManualAsync();
    }

    private static string Hash(string edition) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"Test Module manual, edition {edition}\n")));

    private IIndustryModule LoadedTestModule() =>
        _factory.Services.GetRequiredService<IReadOnlyList<ModuleLoader.LoadedModule>>()
            .Single(m => m.Module.ModuleId == "test-module").Module;

    private async Task<ManualFilingOutcome> FileAsync(Guid tenantId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        return await scope.Services.GetRequiredService<ManualFiler>().FileModuleManualAsync(LoadedTestModule());
    }

    // The startup path for ONE tenant — the whole sweep would also file into every other test's tenants.
    private Task StartupAsync(Guid tenantId) =>
        ManualStartupFiling.FileForTenantAsync(_factory.Services, tenantId, [LoadedTestModule()], NullLogger.Instance);

    private async Task<ModuleActivation> ActivationAsync(Guid tenantId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        return await scope.Db.ModuleActivations.AsNoTracking().SingleAsync(a => a.ModuleId == "test-module");
    }

    private async Task<(Document Document, List<DocumentVersion> Versions)> DocumentAsync(Guid tenantId, Guid documentId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        var document = await scope.Db.Documents.AsNoTracking().SingleAsync(d => d.Id == documentId);
        var versions = await scope.Db.DocumentVersions.AsNoTracking()
            .Where(v => v.DocumentId == documentId && v.Status == DocumentVersionStatus.Confirmed)
            .OrderBy(v => v.VersionNumber)
            .ToListAsync();
        return (document, versions);
    }

    private async Task MakeStandardAsync(Guid tenantId, Guid repositoryId)
    {
        // A tenant seeded straight into the database has no standard repository (provisioning sets it); give it one.
        await using var scope = await TenantScopeAsync(tenantId);
        var tenant = await scope.Db.Tenants.SingleAsync(t => t.Id == tenantId);
        tenant.StandardRepositoryId = repositoryId;
        await scope.Db.SaveChangesAsync();
    }

    private async Task<Document> ManualsFolderAsync(Guid tenantId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        return await scope.Db.Documents.AsNoTracking()
            .SingleAsync(d => scope.Db.MaskVersions.Any(v => v.Id == d.MaskVersionId && v.MaskId == SimplArchive.Domain.Masks.WellKnownMaskIds.Documentation));
    }

    private async Task<string?> FieldAsync(Guid tenantId, Guid documentId, string name)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        return await scope.Db.FieldValues
            .Where(v => v.DocumentId == documentId && scope.Db.FieldDefinitions.Any(f => f.Id == v.FieldDefinitionId && f.Name == name))
            .Select(v => v.Value)
            .SingleOrDefaultAsync();
    }

    private async Task SoftDeleteAsync(Guid tenantId, Guid documentId)
    {
        await using var scope = await TenantScopeAsync(tenantId);
        var document = await scope.Db.Documents.SingleAsync(d => d.Id == documentId);
        document.DeletedAt = DateTimeOffset.UtcNow;
        await scope.Db.SaveChangesAsync();
    }

    private sealed class TenantScope(AsyncServiceScope scope) : IAsyncDisposable
    {
        public IServiceProvider Services => scope.ServiceProvider;

        public SimplArchiveDbContext Db => scope.ServiceProvider.GetRequiredService<SimplArchiveDbContext>();

        public ValueTask DisposeAsync() => scope.DisposeAsync();
    }

    private Task<TenantScope> TenantScopeAsync(Guid tenantId)
    {
        var scope = _factory.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<CurrentTenantAccessor>().TenantId = tenantId;
        return Task.FromResult(new TenantScope(scope));
    }

    private sealed record Rig(HttpClient Admin, HttpClient Owner, Guid TenantId, Guid RepoId);

    private async Task<Rig> RigAsync()
    {
        var (clientId, secret, tenantId) = await _factory.SeedServiceAccountAsync(canManageRepositories: true);
        var owner = _factory.CreateAuthedClient(await _factory.GetTokenAsync(clientId, secret));
        var repoId = (await TestJson.Post(owner, "/api/repositories", new { name = $"Manuals {Guid.NewGuid():N}" }))
            .GetProperty("id").GetGuid();

        var email = $"manuals-{Guid.NewGuid():N}@e2e.local";
        const string password = "manuals12345678";
        var adminId = await _factory.SeedUserAsync(tenantId, email, password, "Manuals Admin", isTenantAdmin: true);
        await TestJson.Put(owner, $"/api/documents/{repoId}/acl-entries/users/{adminId}",
            new { canSee = true, canReadContent = true, canCreateSubItems = true, canEditContent = true });

        var admin = _factory.CreateAuthedClient(await _factory.GetUserTokenAsync(email, password));
        return new Rig(admin, owner, tenantId, repoId);
    }

    private static async Task ActivateAsync(Rig rig, ECDsa vendorKey)
    {
        var license = new TenantLicense(["test-module"], rig.TenantId,
                DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)), ModuleAbiVersion.Major, string.Empty)
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
        await TestJson.Put(rig.Admin, "/api/modules/test-module/license", new { licenseDocumentId = docId });
    }
}

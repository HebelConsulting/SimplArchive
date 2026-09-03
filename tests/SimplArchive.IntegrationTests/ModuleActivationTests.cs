using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// The activation act against the real DbContext (ADRs 0740/0743): a verified license seeds the module's
// masks and upserts ONE row per (tenant, module) — renewal replaces, never accumulates — and a refused
// license writes nothing at all.
public class ModuleActivationTests
{
    private sealed class TestUserAccessor : SimplArchive.Application.Abstractions.ICurrentUserAccessor
    {
        public Guid? UserId { get; set; }
    }

    private sealed class TestServiceAccountAccessor : SimplArchive.Application.Abstractions.ICurrentServiceAccountAccessor
    {
        public Guid? ServiceAccountId { get; set; }
    }

    private static ModuleActivationService CreateService(SimplArchiveDbContext context, Guid userId) =>
        new(context,
            new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance),
            new ModuleArchiveFacade(context, new TestUserAccessor { UserId = userId }, new TestServiceAccountAccessor()));

    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid? tenantId = null)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;
        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private static async Task<(Guid TenantId, Guid UserId)> SeedTenantAsync(SqliteConnection connection)
    {
        using (var setup = CreateContext(connection))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var seed = CreateContext(connection);
        seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Tenant", CreatedAt = DateTimeOffset.UtcNow });
        seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
        await seed.SaveChangesAsync();
        return (tenantId, userId);
    }

    private static async Task<Guid> FileDocumentAsync(SimplArchiveDbContext context, Guid tenantId, Guid userId, string name, Guid? maskVersionId = null)
    {
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.Documents.Add(document);
        await context.SaveChangesAsync();
        return document.Id;
    }

    private static string LicenseJson(ECDsa key, Guid tenantId, DateOnly end)
    {
        var license = new ModuleLicense("test-module", tenantId, end, ModuleAbiVersion.Major, string.Empty).Sign(key);
        return System.Text.Json.JsonSerializer.Serialize(license, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    [Fact]
    public async Task Activation_seeds_the_masks_and_writes_the_row()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();
        var testModule = new TestModule.TestModule();

        Guid licenseDocumentId;
        using (var context = CreateContext(connection, tenantId))
        {
            await new WellKnownMaskSeeder(context, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            licenseDocumentId = await FileDocumentAsync(context, tenantId, userId, "flight-school-2027.json");
            var service = CreateService(context, userId);
            var activation = await service.ActivateAsync(
                testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2027, 3, 1)), licenseDocumentId, tenantId, userId);

            Assert.Equal("test-module", activation.ModuleId);
            Assert.Equal(licenseDocumentId, activation.LicenseDocumentId);
            Assert.Equal(userId, activation.ActivatedByUserId);
        }

        using var check = CreateContext(connection, tenantId);
        Assert.Single(await check.ModuleActivations.ToListAsync());
        // The seeder ran: the module's masks are planted (activated in name only would be a lie).
        Assert.NotNull(await check.Masks.SingleOrDefaultAsync(m => m.Id == TestModule.TestModule.CertificateMaskId));

        // The maskless artefact was dressed in the Module-license mask and stamped with the VERIFIED
        // claims — the projection that lets a listing self-describe (the JSON stays the only truth).
        var stamped = await check.Documents.SingleAsync(d => d.Id == licenseDocumentId);
        Assert.True(await check.MaskVersions.AnyAsync(v => v.Id == stamped.MaskVersionId && v.MaskId == WellKnownMaskIds.ModuleLicense));
        var values = await check.FieldValues
            .Where(v => v.DocumentId == licenseDocumentId)
            .Join(check.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
            .ToListAsync();
        Assert.Equal("test-module", values.Single(v => v.Name == "Module").Value);
        Assert.Equal("2027-03-01", values.Single(v => v.Name == "Valid until").Value);
    }

    [Fact]
    public async Task A_license_document_wearing_another_mask_is_not_redressed()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();
        var testModule = new TestModule.TestModule();

        using var context = CreateContext(connection, tenantId);
        await new WellKnownMaskSeeder(context, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        var basicVersionId = (await context.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent)).Id;
        var documentId = await FileDocumentAsync(context, tenantId, userId, "license-as-basic-entry.json", basicVersionId);

        var service = CreateService(context, userId);
        await service.ActivateAsync(
            testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2027, 3, 1)), documentId, tenantId, userId);

        // The administrator's own typing choice stands: the mask is untouched and nothing was stamped —
        // but the ACTIVATION itself succeeded regardless, because the projection is best-effort.
        var document = await context.Documents.SingleAsync(d => d.Id == documentId);
        Assert.Equal(basicVersionId, document.MaskVersionId);
        Assert.Empty(await context.FieldValues.Where(v => v.DocumentId == documentId).ToListAsync());
        Assert.Single(await context.ModuleActivations.ToListAsync());
    }

    [Fact]
    public async Task Renewal_replaces_the_row_rather_than_accumulating()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();
        var testModule = new TestModule.TestModule();

        using var context = CreateContext(connection, tenantId);
        var service = CreateService(context, userId);
        var firstDocumentId = await FileDocumentAsync(context, tenantId, userId, "license-2026.json");
        await service.ActivateAsync(testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2026, 12, 31)), firstDocumentId, tenantId, userId);

        var renewalDocumentId = await FileDocumentAsync(context, tenantId, userId, "license-2027.json");
        var renewed = await service.ActivateAsync(
            testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2027, 12, 31)), renewalDocumentId, tenantId, userId);

        Assert.Single(await context.ModuleActivations.ToListAsync());
        Assert.Equal(new DateTimeOffset(2027, 12, 31, 0, 0, 0, TimeSpan.Zero), renewed.SupportContractEndDate);
        Assert.Equal(renewalDocumentId, renewed.LicenseDocumentId);
    }

    [Fact]
    public async Task A_refused_license_writes_nothing()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();
        var testModule = new TestModule.TestModule();

        using var context = CreateContext(connection, tenantId);
        var service = CreateService(context, userId);

        // Bound to a DIFFERENT tenant — the per-tenant binding is the whole point of v0 (ADR 0743).
        var documentId = await FileDocumentAsync(context, tenantId, userId, "wrong-tenant.json");
        await Assert.ThrowsAsync<ModuleLicenseException>(() => service.ActivateAsync(
            testModule, LicenseJson(vendorKey, Guid.NewGuid(), new DateOnly(2027, 3, 1)), documentId, tenantId, userId));

        Assert.Empty(await context.ModuleActivations.ToListAsync());
        Assert.Null(await context.Masks.SingleOrDefaultAsync(m => m.Id == TestModule.TestModule.CertificateMaskId));
    }
}

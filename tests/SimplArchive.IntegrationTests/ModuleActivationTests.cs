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

        // The maskless artefact was dressed in the License mask and stamped with the VERIFIED
        // claims — the projection that lets a listing self-describe (the JSON stays the only truth).
        var stamped = await check.Documents.SingleAsync(d => d.Id == licenseDocumentId);
        Assert.True(await check.MaskVersions.AnyAsync(v => v.Id == stamped.MaskVersionId && v.MaskId == WellKnownMaskIds.License));
        var values = await check.FieldValues
            .Where(v => v.DocumentId == licenseDocumentId)
            .Join(check.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
            .ToListAsync();
        Assert.Equal("test-module", values.Single(v => v.Name == "Module").Value);
        Assert.Equal("2027-03-01", values.Single(v => v.Name == "Valid until").Value);
    }

    [Fact]
    public async Task A_license_wearing_a_default_mask_is_redressed_and_stamped()
    {
        // The REAL filing path never ends maskless: finalize stamps Basic Entry on every content upload
        // (and a bare create stamps Folder). The original rule respected any worn mask, which made the
        // stamp unreachable in practice — both demos' license documents sat as unstamped Basic Entry
        // while the maskless-only unit test stayed green (found 2026-09-08). The predecessor of THIS
        // test enshrined that assumption: it used Basic Entry as its example of "the administrator's own
        // typing choice".
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

        var document = await context.Documents.SingleAsync(d => d.Id == documentId);
        Assert.True(await context.MaskVersions.AnyAsync(v => v.Id == document.MaskVersionId && v.MaskId == WellKnownMaskIds.License));
        var values = await context.FieldValues
            .Where(v => v.DocumentId == documentId)
            .Join(context.FieldDefinitions, v => v.FieldDefinitionId, f => f.Id, (v, f) => new { f.Name, v.Value })
            .ToListAsync();
        Assert.Equal("test-module", values.Single(v => v.Name == "Module").Value);
        Assert.Equal("2027-03-01", values.Single(v => v.Name == "Valid until").Value);
    }

    [Fact]
    public async Task A_license_deliberately_wearing_a_third_mask_is_not_redressed()
    {
        // The projection still must not fight a mask someone actually CHOSE — the eMail mask can only be
        // on this document because a person or a rule put it there, unlike the two filing-path defaults.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();
        var testModule = new TestModule.TestModule();

        using var context = CreateContext(connection, tenantId);
        await new WellKnownMaskSeeder(context, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
        var emailVersionId = (await context.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.EMail && v.IsCurrent)).Id;
        // eMail has required fields (ADR 0176 fires on assignment), so type the document the way a
        // person would: values first, mask second — every required field, so the test does not chase
        // the mask's field list.
        var documentId = await FileDocumentAsync(context, tenantId, userId, "license-typed-as-email.json");
        var requiredFieldIds = await context.FieldDefinitions
            .Where(f => f.MaskVersionId == emailVersionId && f.IsRequired)
            .Select(f => f.Id)
            .ToListAsync();
        foreach (var fieldId in requiredFieldIds)
        {
            context.FieldValues.Add(new FieldValue
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                DocumentId = documentId,
                FieldDefinitionId = fieldId,
                Value = "deliberately typed as mail",
            });
        }

        (await context.Documents.SingleAsync(d => d.Id == documentId)).MaskVersionId = emailVersionId;
        await context.SaveChangesAsync();

        var service = CreateService(context, userId);
        await service.ActivateAsync(
            testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2027, 3, 1)), documentId, tenantId, userId);

        var document = await context.Documents.SingleAsync(d => d.Id == documentId);
        Assert.Equal(emailVersionId, document.MaskVersionId);
        Assert.Equal(requiredFieldIds.Count, await context.FieldValues.CountAsync(v => v.DocumentId == documentId)); // nothing stamped on top
        Assert.Single(await context.ModuleActivations.ToListAsync()); // activation itself is never gated on the projection
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

    [Fact]
    public async Task The_activation_records_which_key_verified_it()
    {
        // The rotation record (ABI 0.25, ADR 0793) reaching the ROW, not just the verifier's return value: it
        // is what turns "who is on the compromised key" into a query.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var vendorKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        TestModule.TestModule.VerifyKeyPem = vendorKey.ExportSubjectPublicKeyInfoPem();

        using (var context = CreateContext(connection, tenantId))
        {
            await new WellKnownMaskSeeder(context, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            var documentId = await FileDocumentAsync(context, tenantId, userId, "licensed.json");
            await CreateService(context, userId).ActivateAsync(
                new TestModule.TestModule(), LicenseJson(vendorKey, tenantId, new DateOnly(2027, 3, 1)), documentId, tenantId, userId);
        }

        using var check = CreateContext(connection, tenantId);
        var activation = await check.ModuleActivations.SingleAsync();
        Assert.Equal(
            ModuleLicenseVerifier.KeyThumbprint(vendorKey.ExportSubjectPublicKeyInfoPem()),
            activation.VerifiedByKeyThumbprint);
    }

    [Fact]
    public async Task A_renewal_signed_by_the_new_key_moves_the_row_off_the_old_one()
    {
        // The half that makes the record USEFUL: a tenant who has renewed under the new key must stop counting
        // as affected, or the rotation would look permanently incomplete.
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, userId) = await SeedTenantAsync(connection);
        using var retiring = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var current = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var module = new RotatingModule(current.ExportSubjectPublicKeyInfoPem(), retiring.ExportSubjectPublicKeyInfoPem());

        using (var context = CreateContext(connection, tenantId))
        {
            await new WellKnownMaskSeeder(context, NullLogger<WellKnownMaskSeeder>.Instance).EnsureWellKnownMasksAsync(tenantId);
            var service = CreateService(context, userId);

            // Activated during the overlap on the RETIRING key…
            var oldDocumentId = await FileDocumentAsync(context, tenantId, userId, "licensed-old-key.json");
            await service.ActivateAsync(module, LicenseJson(retiring, tenantId, new DateOnly(2027, 3, 1)), oldDocumentId, tenantId, userId);
            Assert.Equal(
                ModuleLicenseVerifier.KeyThumbprint(retiring.ExportSubjectPublicKeyInfoPem()),
                (await context.ModuleActivations.SingleAsync()).VerifiedByKeyThumbprint);

            // …then renewed on the CURRENT one.
            var newDocumentId = await FileDocumentAsync(context, tenantId, userId, "licensed-new-key.json");
            await service.ActivateAsync(module, LicenseJson(current, tenantId, new DateOnly(2028, 3, 1)), newDocumentId, tenantId, userId);
        }

        using var check = CreateContext(connection, tenantId);
        var activation = await check.ModuleActivations.SingleAsync(); // still ONE row — renewal replaces
        Assert.Equal(
            ModuleLicenseVerifier.KeyThumbprint(current.ExportSubjectPublicKeyInfoPem()),
            activation.VerifiedByKeyThumbprint);
    }

    // A module mid-rotation: two verify keys, newest first. Its ModuleId matches LicenseJson's so the same
    // helper signs for it; it contributes no masks, which keeps this about the activation row.
    private sealed class RotatingModule(params string[] keysPem) : IIndustryModule
    {
        public string ModuleId => "test-module";

        public string DisplayName => "Rotating module";

        public int AbiMajorVersion => ModuleAbiVersion.Major;

        public string LicenseVerifyKeyPem => keysPem[0];

        public IReadOnlyList<string> LicenseVerifyKeysPem { get; } = keysPem;

        public IReadOnlyList<ModuleMaskSeed> Masks => [];

        public void ConfigureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
        {
        }
    }
}

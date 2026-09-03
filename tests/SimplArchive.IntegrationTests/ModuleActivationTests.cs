using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

// The activation act against the real DbContext (ADRs 0740/0743): a verified license seeds the module's
// masks and upserts ONE row per (tenant, module) — renewal replaces, never accumulates — and a refused
// license writes nothing at all.
public class ModuleActivationTests
{
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

        var licenseDocumentId = Guid.NewGuid();
        using (var context = CreateContext(connection, tenantId))
        {
            var service = new ModuleActivationService(context, new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance));
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
        var service = new ModuleActivationService(context, new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance));
        await service.ActivateAsync(testModule, LicenseJson(vendorKey, tenantId, new DateOnly(2026, 12, 31)), Guid.NewGuid(), tenantId, userId);

        var renewalDocumentId = Guid.NewGuid();
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
        var service = new ModuleActivationService(context, new ModuleMaskSeeder(context, NullLogger<ModuleMaskSeeder>.Instance));

        // Bound to a DIFFERENT tenant — the per-tenant binding is the whole point of v0 (ADR 0743).
        await Assert.ThrowsAsync<ModuleLicenseException>(() => service.ActivateAsync(
            testModule, LicenseJson(vendorKey, Guid.NewGuid(), new DateOnly(2027, 3, 1)), Guid.NewGuid(), tenantId, userId));

        Assert.Empty(await context.ModuleActivations.ToListAsync());
        Assert.Null(await context.Masks.SingleOrDefaultAsync(m => m.Id == TestModule.TestModule.CertificateMaskId));
    }
}

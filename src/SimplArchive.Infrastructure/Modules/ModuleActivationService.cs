using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Modules;
using SimplArchive.ModuleAbi;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The activation act (ADRs 0740/0743): a verified license seeds the module's masks and upserts the
/// tenant's activation row — one row per (tenant, module), renewal replacing the end date and the license
/// pointer. Verification failures throw <see cref="ModuleLicenseException"/> before anything is written.
/// </summary>
/// <remarks>
/// The caller (the controller) resolves the filed license document and reads its content; this service
/// starts at the JSON so the whole act is provable against the DbContext alone. Auditing likewise stays
/// with the caller — the tenant-settings precedent: the mutation site that owns the action code records it.
/// </remarks>
public sealed class ModuleActivationService
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ModuleMaskSeeder _seeder;

    public ModuleActivationService(SimplArchiveDbContext dbContext, ModuleMaskSeeder seeder)
    {
        _dbContext = dbContext;
        _seeder = seeder;
    }

    public async Task<ModuleActivation> ActivateAsync(
        IIndustryModule module,
        string licenseJson,
        Guid licenseDocumentId,
        Guid tenantId,
        Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        var license = ModuleLicenseVerifier.Parse(licenseJson);
        ModuleLicenseVerifier.Verify(license, module, tenantId);

        // Masks first (idempotent, heals on upgrade) — a tenant whose activation row exists but whose
        // masks are missing would be activated in name only.
        await _seeder.SeedAsync(module, tenantId, cancellationToken);

        var activation = await _dbContext.ModuleActivations
            .SingleOrDefaultAsync(a => a.ModuleId == module.ModuleId, cancellationToken);
        if (activation is null)
        {
            activation = new ModuleActivation
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ModuleId = module.ModuleId,
            };
            _dbContext.ModuleActivations.Add(activation);
        }

        activation.SupportContractEndDate = new DateTimeOffset(license.SupportContractEnd, TimeOnly.MinValue, TimeSpan.Zero);
        activation.LicenseDocumentId = licenseDocumentId;
        activation.ActivatedByUserId = actorUserId;
        activation.ActivatedAt = DateTimeOffset.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return activation;
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Masks;
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
    private readonly IModuleArchiveFacade _archive;

    public ModuleActivationService(SimplArchiveDbContext dbContext, ModuleMaskSeeder seeder, IModuleArchiveFacade archive)
    {
        _dbContext = dbContext;
        _seeder = seeder;
        _archive = archive;
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

        await StampLicenseDocumentAsync(licenseDocumentId, license, cancellationToken);
        return activation;
    }

    /// <summary>
    /// Projects the VERIFIED claims onto the license document's index fields (Module, Valid until) so a
    /// listing self-describes — the booking→appointment lockstep shape: the signed JSON stays the only
    /// truth, the fields are its projection. A maskless document is dressed in the well-known
    /// Module-license mask first; one deliberately wearing something ELSE is left alone — the projection
    /// must not fight the administrator's own typing choice.
    /// </summary>
    private async Task StampLicenseDocumentAsync(Guid licenseDocumentId, ModuleLicense license, CancellationToken cancellationToken)
    {
        var document = await _dbContext.Documents.SingleAsync(d => d.Id == licenseDocumentId, cancellationToken);
        if (document.MaskVersionId is { } wornVersionId)
        {
            var wearsLicenseMask = await _dbContext.MaskVersions
                .AnyAsync(v => v.Id == wornVersionId && v.MaskId == WellKnownMaskIds.ModuleLicense, cancellationToken);
            if (!wearsLicenseMask)
            {
                return;
            }
        }
        else
        {
            var currentVersionId = await _dbContext.MaskVersions
                .Where(v => v.MaskId == WellKnownMaskIds.ModuleLicense && v.IsCurrent)
                .Select(v => (Guid?)v.Id)
                .SingleOrDefaultAsync(cancellationToken);
            if (currentVersionId is null)
            {
                return; // tenant not yet healed to carry the mask — the projection is best-effort, the activation is not.
            }

            document.MaskVersionId = currentVersionId;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        await _archive.SetFieldsAsync(licenseDocumentId, new Dictionary<string, string>
        {
            ["Module"] = license.ModuleId,
            ["Valid until"] = license.SupportContractEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        }, cancellationToken);
    }
}

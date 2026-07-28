using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Masks;

// See ADR "Mask creation endpoint". VersionNumber/IsCurrent on the created MaskVersion are left unset —
// SimplArchiveDbContext.SaveChanges assigns them automatically (ADR "Mask name uniqueness across
// versions"), same precedent as every other MaskVersion creation path.
public class WellKnownMaskSeeder : IWellKnownMaskSeeder
{
    private readonly SimplArchiveDbContext _dbContext;

    public WellKnownMaskSeeder(SimplArchiveDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    private record FieldSpec(string Name, FieldDataType DataType, bool IsRequired);

    public async Task EnsureWellKnownMasksAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.Folder, "Folder", [], cancellationToken);

        // "Short Description" and "Doc Date" were removed — the former duplicates Document.Name (a document
        // is named after its file, ADR "Drag-and-drop document upload"), the latter duplicates the real
        // DocumentVersion.DocumentDate issuing date (ADR "System-field search"). See ADR "Drop redundant
        // Short Description / Doc Date mask fields".
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.BasicEntry, "Basic Entry",
        [
            new FieldSpec("Keywords", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);

        // Filled automatically on upload of an .eml/.msg (ADR "Email auto-classification"). "Entry ID" is
        // the RFC 5322 Message-ID; Cc/Date/Entry ID are optional (not every message has them).
        await EnsureMaskAsync(tenantId, WellKnownMaskIds.EMail, "eMail",
        [
            new FieldSpec("From", FieldDataType.Text, IsRequired: true),
            new FieldSpec("To", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Cc", FieldDataType.Text, IsRequired: false),
            new FieldSpec("Subject", FieldDataType.Text, IsRequired: true),
            new FieldSpec("Date", FieldDataType.Date, IsRequired: false),
            new FieldSpec("Entry ID", FieldDataType.Text, IsRequired: false),
        ], cancellationToken);
    }

    private async Task EnsureMaskAsync(Guid tenantId, Guid maskId, string name, IReadOnlyList<FieldSpec> fields, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters(["TenantFilter"]) — this Where clause is already explicitly scoped by the
        // tenantId parameter, so the automatic tenant filter is redundant here and, worse, wrong whenever
        // the caller has no ICurrentTenantAccessor.TenantId set (e.g. a PlatformAdministrator creating a
        // brand-new tenant, ADR "Tenant onboarding and platform-admin mechanism") — that filter's
        // predicate is `TenantId == null`, which never matches any real row, making this check always
        // report "not found" regardless of the real data.
        if (await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"]).AnyAsync(m => m.TenantId == tenantId && m.Id == maskId, cancellationToken))
        {
            return;
        }

        _dbContext.Masks.Add(new Mask { Id = maskId, TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow });

        var maskVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = maskId, Name = name, CreatedAt = DateTimeOffset.UtcNow };
        _dbContext.MaskVersions.Add(maskVersion);

        foreach (var field in fields)
        {
            _dbContext.FieldDefinitions.Add(new FieldDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskVersionId = maskVersion.Id,
                Name = field.Name,
                DataType = field.DataType,
                IsRequired = field.IsRequired,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}

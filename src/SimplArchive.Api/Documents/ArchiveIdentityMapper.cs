using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

internal readonly record struct PrincipalRef(Guid? UserId, Guid? ServiceAccountId);

internal sealed record ArchiveMask(Guid MaskId, bool WellKnown, ArchiveMaskVersion Version, List<ArchiveField> Fields);
internal sealed record ArchiveMaskVersion(Guid MaskVersionId, string Name, int VersionNumber, int? ReviewSlaDays, int? RetentionYears, string? DefaultSensitivityLabel);
// `IsList` is ADDITIVE and FormatVersion deliberately stays 2 — the same call issue #383's "mentions" made.
// An archive written before #703 simply has no `isList` property, which deserializes to false: exactly
// what every field in it was. Bumping the version would refuse those archives to gain nothing (#703).
internal sealed record ArchiveField(Guid FieldDefinitionId, string Name, int DataType, bool IsRequired, bool IsList, string? FormatPattern, int? MaxTextLength, string? MinValue, string? MaxValue);
internal sealed record ArchivePrincipals(List<ArchiveUser> Users, List<ArchiveServiceAccount> ServiceAccounts, List<ArchiveGroup> Groups, List<ArchiveMembership>? Memberships)
{
    public List<ArchiveMembership> Memberships { get; init; } = Memberships ?? [];
}
internal sealed record ArchiveUser(Guid Id, string Email, string DisplayName, bool IsActive, int? ClearanceRank);
internal sealed record ArchiveServiceAccount(Guid Id, string Name, bool IsActive, int? ClearanceRank);
internal sealed record ArchiveGroup(Guid Id, string Name, int? ClearanceRank);
internal sealed record ArchiveLabel(string Name, int Rank, string? Color, bool Watermark);
internal sealed record ArchiveMembership(Guid GroupId, Guid UserId);

/// <summary>
/// Answers "who, or what, is this archive's principal / mask / label in THIS tenant?" for an import.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <see cref="RepositoryImporter"/>, which was on the 1000-line debt list. Resolving identities
/// across two tenants is a different job from walking a zip and writing documents: every method here answers a
/// question about the DESTINATION tenant, and none of them knows about archive entries, blobs, the four import
/// phases, or the single transaction the importer runs them inside.
/// </para>
/// <para>
/// Every mapping is match-or-placeholder, and the placeholder is always DEACTIVATED. An ACL naming a group the
/// target tenant does not have must survive the import — otherwise a permission silently disappears — but a
/// principal nobody has vouched for must not be able to sign in, so the row exists and cannot be used until an
/// administrator says otherwise (ADR "ACL in export/import").
/// </para>
/// <para>
/// <c>ResolveCreator</c> deliberately did NOT move. It attributes an unmapped creator to the IMPORTING admin,
/// which the importer learns after construction via <c>SetImporter</c> — so it reads the importer's own mutable
/// state rather than answering a question about the archive. Pulling it here would mean threading that state
/// into this class purely to keep a method family together (ADR 0730).
/// </para>
/// </remarks>
internal sealed class ArchiveIdentityMapper(SimplArchiveDbContext dbContext)
{
    private readonly SimplArchiveDbContext _dbContext = dbContext;

    // Matches each archived group by name, creating a deactivated (empty) placeholder if absent — so an ACL grant
    // to a group survives the import even when the target tenant doesn't have that group yet. Memberships are a
    // tenant concern and aren't imported (ADR "ACL in export/import").
    internal async Task<Dictionary<Guid, Guid>> MapGroupsAsync(List<ArchiveGroup> groups, Guid tenantId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var group in groups)
        {
            var existing = await _dbContext.Groups.FirstOrDefaultAsync(g => g.Name == group.Name && g.ParentGroupId == null, cancellationToken);
            if (existing is null)
            {
                existing = new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = group.Name, CreatedAt = DateTimeOffset.UtcNow };
                _dbContext.Groups.Add(existing);
            }

            // Clearance travels with permissions, applied max-never-lower (ADR "Classification in export/import").
            if (group.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[group.Id] = existing.Id;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return map;
    }

    // Ensures each archived sensitivity label exists in the destination tenant (created by name if absent, with
    // the archived rank/colour/watermark; an existing label's config is left untouched), returning a name → id map
    // — see ADR "Classification in export/import". Committed here so documents + mask defaults can reference the ids.
    internal async Task<Dictionary<string, Guid>> EnsureLabelsAsync(List<ArchiveLabel> labels, Guid tenantId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (labels.Count == 0)
        {
            return map;
        }

        var existing = await _dbContext.SensitivityLabelDefinitions.ToDictionaryAsync(l => l.Name, l => l, cancellationToken);
        foreach (var label in labels)
        {
            if (!existing.TryGetValue(label.Name, out var entity))
            {
                entity = new SimplArchive.Domain.Documents.SensitivityLabelDefinition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    Name = label.Name,
                    Rank = label.Rank,
                    Color = label.Color,
                    Watermark = label.Watermark,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _dbContext.SensitivityLabelDefinitions.Add(entity);
                existing[label.Name] = entity;
            }

            map[label.Name] = entity.Id;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return map;
    }

    // Matches each archived user by email (else a deactivated placeholder) and each service account by name (else
    // a deactivated placeholder with an inert client id). Returns archive-principal-id → target-id, keyed for
    // both principal kinds (ids are Guids, so one map is unambiguous).
    internal async Task<Dictionary<Guid, PrincipalRef>> MapPrincipalsAsync(ArchivePrincipals principals, Guid tenantId, bool includePermissions, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, PrincipalRef>();

        foreach (var user in principals.Users)
        {
            var normalized = user.Email.ToUpperInvariant();
            var existing = await _dbContext.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
            if (existing is null)
            {
                existing = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = user.Email, DisplayName = user.DisplayName, IsActive = false, CreatedAt = DateTimeOffset.UtcNow, ImapShowAllDocuments = await _dbContext.Tenants.Where(t => t.Id == tenantId).Select(t => t.ImapShowAllDocumentsDefault).SingleAsync(cancellationToken) };
                _dbContext.Users.Add(existing);
            }

            // Clearance travels with permissions, applied max-never-lower (ADR "Classification in export/import").
            if (includePermissions && user.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[user.Id] = new PrincipalRef(existing.Id, null);
        }

        foreach (var svc in principals.ServiceAccounts)
        {
            var existing = await _dbContext.ServiceAccounts.FirstOrDefaultAsync(s => s.Name == svc.Name, cancellationToken);
            if (existing is null)
            {
                existing = new ServiceAccount { Id = Guid.NewGuid(), TenantId = tenantId, Name = svc.Name, OpenIddictApplicationClientId = $"imported:{Guid.NewGuid():N}", IsActive = false, CreatedAt = DateTimeOffset.UtcNow };
                _dbContext.ServiceAccounts.Add(existing);
            }

            if (includePermissions && svc.ClearanceRank is { } rank)
            {
                existing.ClearanceRank = Math.Max(existing.ClearanceRank, rank);
            }

            map[svc.Id] = new PrincipalRef(null, existing.Id);
        }

        return map;
    }

    // Well-known masks merge into the target's current version (fields matched by name); custom masks are created
    // fresh. Returns archive-MaskVersionId → target-MaskVersionId and archive-FieldDefinitionId → target-FieldDefinitionId.
    internal async Task<(Dictionary<Guid, Guid> MaskVersions, Dictionary<Guid, Guid> Fields)> MapMasksAsync(List<ArchiveMask> masks, Guid tenantId, IReadOnlyDictionary<string, Guid> labelMap, CancellationToken cancellationToken)
    {
        var maskVersionMap = new Dictionary<Guid, Guid>();
        var fieldMap = new Dictionary<Guid, Guid>();
        var takenNames = await _dbContext.MaskVersions.Where(m => m.IsCurrent).Select(m => m.Name).ToListAsync(cancellationToken);

        foreach (var mask in masks)
        {
            if (mask.WellKnown)
            {
                var current = await _dbContext.MaskVersions.FirstOrDefaultAsync(m => m.MaskId == mask.MaskId && m.IsCurrent, cancellationToken);
                if (current is null)
                {
                    continue; // the well-known mask isn't present (shouldn't happen after seeding) — drop the mapping
                }

                maskVersionMap[mask.Version.MaskVersionId] = current.Id;
                var targetFields = await _dbContext.FieldDefinitions.Where(f => f.MaskVersionId == current.Id).ToListAsync(cancellationToken);
                foreach (var field in mask.Fields)
                {
                    if (targetFields.FirstOrDefault(t => t.Name == field.Name) is { } match)
                    {
                        fieldMap[field.FieldDefinitionId] = match.Id;
                    }
                }

                continue;
            }

            var newMask = new Mask { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
            var name = RepositoryImporter.UniqueName(mask.Version.Name, takenNames);
            takenNames.Add(name);
            // A custom mask's default sensitivity label (ADR "Classification in export/import") resolves by name;
            // a well-known mask (merged above) keeps the destination's own default rather than being overwritten.
            var defaultLabelId = mask.Version.DefaultSensitivityLabel is { } dl && labelMap.TryGetValue(dl, out var lid) ? lid : (Guid?)null;
            var newVersion = new MaskVersion { Id = Guid.NewGuid(), TenantId = tenantId, MaskId = newMask.Id, Name = name, ReviewSlaDays = mask.Version.ReviewSlaDays, RetentionYears = mask.Version.RetentionYears, DefaultSensitivityLabelId = defaultLabelId, CreatedAt = DateTimeOffset.UtcNow };
            _dbContext.Masks.Add(newMask);
            _dbContext.MaskVersions.Add(newVersion);
            maskVersionMap[mask.Version.MaskVersionId] = newVersion.Id;

            foreach (var field in mask.Fields)
            {
                var newField = new FieldDefinition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MaskVersionId = newVersion.Id,
                    Name = field.Name,
                    DataType = (FieldDataType)field.DataType,
                    IsRequired = field.IsRequired,
                    IsList = field.IsList,
                    FormatPattern = field.FormatPattern,
                    MaxTextLength = field.MaxTextLength,
                    MinValue = field.MinValue,
                    MaxValue = field.MaxValue,
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                _dbContext.FieldDefinitions.Add(newField);
                fieldMap[field.FieldDefinitionId] = newField.Id;
            }
        }

        return (maskVersionMap, fieldMap);
    }
}

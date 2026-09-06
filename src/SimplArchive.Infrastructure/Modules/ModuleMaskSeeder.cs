using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Seeds a module's masks into one tenant (ADRs 0740/0741) — the declarative half of activation, run at
/// activation and healed on upgrade the way the core's own well-known masks are. Idempotent; the masks are
/// permanent tenant data thereafter (deactivation removes behaviour, never masks).
/// </summary>
public sealed class ModuleMaskSeeder
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ILogger<ModuleMaskSeeder> _logger;

    public ModuleMaskSeeder(SimplArchiveDbContext dbContext, ILogger<ModuleMaskSeeder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task SeedAsync(IIndustryModule module, Guid tenantId, CancellationToken cancellationToken = default)
    {
        // Masks first, containment second: a declaration may name a SIBLING mask of the same module (an
        // aircraft admitting its logbook), which does not exist until the first pass has run.
        foreach (var seed in module.Masks)
        {
            await EnsureMaskAsync(module.ModuleId, seed, tenantId, cancellationToken);
        }

        foreach (var seed in module.Masks)
        {
            await ReconcileContainmentAsync(module.ModuleId, seed, tenantId, cancellationToken);
        }
    }

    private async Task EnsureMaskAsync(string moduleId, ModuleMaskSeed seed, Guid tenantId, CancellationToken cancellationToken)
    {
        var mask = await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"])
            .SingleOrDefaultAsync(m => m.TenantId == tenantId && m.Id == seed.MaskId, cancellationToken);
        if (mask is null)
        {
            _dbContext.Masks.Add(new Mask
            {
                Id = seed.MaskId,
                TenantId = tenantId,
                CreatedAt = DateTimeOffset.UtcNow,
                IsFolderMask = seed.IsFolderMask,
                IsBookable = seed.IsBookable,
                AdmitsOnlyDeclaredChildren = seed.AdmitsOnlyDeclaredChildren,
            });

            var version = new MaskVersion
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskId = seed.MaskId,
                Name = seed.Name,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.MaskVersions.Add(version);
            for (var i = 0; i < seed.Fields.Count; i++)
            {
                _dbContext.FieldDefinitions.Add(NewField(version, seed.Fields[i], tenantId, i));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // The heal half (the #664 lesson: a fact added later reaches only new tenants unless the heal
        // carries it too). Structure facts are assigned unconditionally — the module's seed is the
        // authority for its own masks, exactly as the core's well-known table is for the core's.
        if (mask.IsFolderMask != seed.IsFolderMask || mask.IsBookable != seed.IsBookable
            || mask.AdmitsOnlyDeclaredChildren != seed.AdmitsOnlyDeclaredChildren)
        {
            mask.IsFolderMask = seed.IsFolderMask;
            mask.IsBookable = seed.IsBookable;
            mask.AdmitsOnlyDeclaredChildren = seed.AdmitsOnlyDeclaredChildren;
        }

        var current = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(v => v.TenantId == tenantId && v.MaskId == seed.MaskId && v.IsCurrent, cancellationToken);
        var existingFields = await _dbContext.FieldDefinitions.IgnoreQueryFilters(["TenantFilter"])
            .Where(f => f.TenantId == tenantId && f.MaskVersionId == current.Id)
            .ToListAsync(cancellationToken);

        // The seed list's index IS the display order (ADR 0761) — corrected in place, unconditionally, the
        // same way IsFolderMask/IsBookable are above: the module's seed is the authority for its own masks.
        for (var i = 0; i < seed.Fields.Count; i++)
        {
            if (existingFields.FirstOrDefault(f => string.Equals(f.Name, seed.Fields[i].Name, StringComparison.Ordinal)) is { } defined
                && defined.SortOrder != i)
            {
                defined.SortOrder = i;
            }
        }

        for (var i = 0; i < seed.Fields.Count; i++)
        {
            var field = seed.Fields[i];
            if (existingFields.Any(f => string.Equals(f.Name, field.Name, StringComparison.Ordinal)))
            {
                continue;
            }

            if (field.IsRequired)
            {
                // A required field arriving on a worn mask would invalidate every existing document — the
                // same refusal the core's own heal makes, loudly rather than by quiet damage.
                throw new InvalidOperationException(
                    $"Module '{moduleId}' adds REQUIRED field '{field.Name}' to existing mask '{seed.Name}' — "
                    + "a required field cannot be healed onto documents that already exist. Ship it optional, "
                    + "or migrate via a new mask.");
            }

            _logger.LogInformation("Module {ModuleId}: healing field {Field} onto mask {Mask} in tenant {TenantId}.",
                moduleId, field.Name, seed.Name, tenantId);
            _dbContext.FieldDefinitions.Add(NewField(current, field, tenantId, i));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The mask's containment rows, reconciled to the seed (ABI 0.8, ADR 0762): the module is the authority
    /// for its OWN mask's two row families — the children its folder admits, and the parents it may live
    /// under — so missing rows are added and stale ones removed, both scoped strictly to this mask id. The
    /// core's own reconcile cannot fight this: it deletes only rows whose BOTH endpoints are well-known.
    /// A bookable mask's Schedule is derived from IsBookable at rule-load, never written as a row here.
    /// </summary>
    private async Task ReconcileContainmentAsync(string moduleId, ModuleMaskSeed seed, Guid tenantId, CancellationToken cancellationToken)
    {
        // A row may only be written once BOTH masks exist for the tenant (the FK demands it — the core's own
        // containment pass makes the same guard). A declared id with no mask is a module bug worth a Warning
        // rather than a silent skip: the folder would quietly admit less than the module believes.
        var present = (await _dbContext.Masks.IgnoreQueryFilters(["TenantFilter"])
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        HashSet<Guid> Present(IReadOnlyList<Guid>? declared, string family)
        {
            var wanted = (declared ?? []).ToHashSet();
            foreach (var absent in wanted.Where(id => !present.Contains(id)))
            {
                _logger.LogWarning(
                    "Module {ModuleId}: mask {Mask} declares {Family} {Declared}, which this tenant has no mask for — skipped.",
                    moduleId, seed.Name, family, absent);
            }

            wanted.IntersectWith(present);
            return wanted;
        }

        var wantedChildren = Present(seed.AdmittedChildren, "admitted child");
        var existingChildren = await _dbContext.MaskAdmittedChildren.IgnoreQueryFilters(["TenantFilter"])
            .Where(c => c.TenantId == tenantId && c.FolderMaskId == seed.MaskId)
            .ToListAsync(cancellationToken);
        foreach (var childId in wantedChildren.Where(id => existingChildren.All(e => e.ChildMaskId != id)))
        {
            _dbContext.MaskAdmittedChildren.Add(new Domain.Masks.MaskAdmittedChild
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                FolderMaskId = seed.MaskId,
                ChildMaskId = childId,
            });
        }

        _dbContext.MaskAdmittedChildren.RemoveRange(existingChildren.Where(e => !wantedChildren.Contains(e.ChildMaskId)));

        var wantedParents = Present(seed.AllowedParents, "allowed parent");
        var existingParents = await _dbContext.MaskAllowedParents.IgnoreQueryFilters(["TenantFilter"])
            .Where(p => p.TenantId == tenantId && p.MaskId == seed.MaskId)
            .ToListAsync(cancellationToken);
        foreach (var parentId in wantedParents.Where(id => existingParents.All(e => e.ParentMaskId != id)))
        {
            _dbContext.MaskAllowedParents.Add(new Domain.Masks.MaskAllowedParent
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                MaskId = seed.MaskId,
                ParentMaskId = parentId,
            });
        }

        _dbContext.MaskAllowedParents.RemoveRange(existingParents.Where(e => !wantedParents.Contains(e.ParentMaskId)));
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FieldDefinition NewField(MaskVersion version, ModuleFieldSeed field, Guid tenantId, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        MaskVersionId = version.Id,
        SortOrder = sortOrder,
        Name = field.Name,
        DataType = ParseDataType(field.DataType),
        IsRequired = field.IsRequired,
        IsList = field.IsList,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    // By NAME across the boundary (the ABI's deliberate choice): a pinned enum ordinal compiled into a
    // module would re-type stored fields the day the core appends a value. Unknown names are refused
    // loudly — a module written against a newer minor knows types this host does not.
    private static FieldDataType ParseDataType(string name) =>
        Enum.TryParse<FieldDataType>(name, ignoreCase: false, out var type)
            ? type
            : throw new InvalidOperationException($"Unknown field data type '{name}' — this host's ABI knows: {string.Join(", ", Enum.GetNames<FieldDataType>())}.");
}

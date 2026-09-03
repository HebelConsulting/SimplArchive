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
        foreach (var seed in module.Masks)
        {
            await EnsureMaskAsync(module.ModuleId, seed, tenantId, cancellationToken);
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
            foreach (var field in seed.Fields)
            {
                _dbContext.FieldDefinitions.Add(NewField(version, field, tenantId));
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        // The heal half (the #664 lesson: a fact added later reaches only new tenants unless the heal
        // carries it too). Structure facts are assigned unconditionally — the module's seed is the
        // authority for its own masks, exactly as the core's well-known table is for the core's.
        if (mask.IsFolderMask != seed.IsFolderMask || mask.IsBookable != seed.IsBookable)
        {
            mask.IsFolderMask = seed.IsFolderMask;
            mask.IsBookable = seed.IsBookable;
        }

        var current = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .SingleAsync(v => v.TenantId == tenantId && v.MaskId == seed.MaskId && v.IsCurrent, cancellationToken);
        var existingFields = await _dbContext.FieldDefinitions.IgnoreQueryFilters(["TenantFilter"])
            .Where(f => f.TenantId == tenantId && f.MaskVersionId == current.Id)
            .Select(f => f.Name)
            .ToListAsync(cancellationToken);

        foreach (var field in seed.Fields.Where(f => !existingFields.Contains(f.Name, StringComparer.Ordinal)))
        {
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
            _dbContext.FieldDefinitions.Add(NewField(current, field, tenantId));
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static FieldDefinition NewField(MaskVersion version, ModuleFieldSeed field, Guid tenantId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenantId,
        MaskVersionId = version.Id,
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

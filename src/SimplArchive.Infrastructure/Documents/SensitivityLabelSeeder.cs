using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Documents;

// Seeds a tenant's default sensitivity labels (ADR "Configurable sensitivity labels + upload defaults"). The four
// defaults match the ranks/colours of the pre-configurable fixed enum (ADR 0399); Confidential + Restricted are
// watermarked. Idempotent — only inserts labels whose name is missing (uses IgnoreQueryFilters so it works for a
// PlatformAdministrator caller with no ambient tenant, like WellKnownMaskSeeder).
public sealed class SensitivityLabelSeeder : ISensitivityLabelSeeder
{
    // (Name, Rank, Color, Watermark) — Public..Restricted; None is the absence of a label (no row).
    private static readonly (string Name, int Rank, string Color, bool Watermark)[] Defaults =
    [
        ("Public", 1, "#2e7d32", false),
        ("Internal", 2, "#1565c0", false),
        ("Confidential", 3, "#ef6c00", true),
        ("Restricted", 4, "#c62828", true),
    ];

    private readonly SimplArchiveDbContext _dbContext;

    public SensitivityLabelSeeder(SimplArchiveDbContext dbContext) => _dbContext = dbContext;

    public async Task EnsureDefaultLabelsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var existing = await _dbContext.SensitivityLabelDefinitions
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(s => s.TenantId == tenantId)
            .Select(s => s.Name)
            .ToListAsync(cancellationToken);
        var have = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = false;
        foreach (var (name, rank, color, watermark) in Defaults)
        {
            if (have.Contains(name))
            {
                continue;
            }

            _dbContext.SensitivityLabelDefinitions.Add(new SensitivityLabelDefinition
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                Name = name,
                Rank = rank,
                Color = color,
                Watermark = watermark,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            added = true;
        }

        if (added)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }
}

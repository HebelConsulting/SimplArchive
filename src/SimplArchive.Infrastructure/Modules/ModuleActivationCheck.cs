using Microsoft.EntityFrameworkCore;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// The one answer to "is this module ACTIVE for the ambient tenant" (ADRs 0737/0740) — asked by the
/// controller gate, the transitions surface and the root's rel emission. One helper rather than three
/// copies of the derived-active question, because the copies are exactly what would drift on the grace
/// arithmetic.
/// </summary>
public static class ModuleActivationCheck
{
    /// <summary>True when the ambient tenant holds an activation whose derived state is active.</summary>
    public static async Task<bool> IsActiveAsync(
        SimplArchiveDbContext dbContext, string moduleId, DateTimeOffset now, CancellationToken cancellationToken) =>
        await dbContext.ModuleActivations.FirstOrDefaultAsync(a => a.ModuleId == moduleId, cancellationToken)
            is { } activation && ModuleActivationPolicy.IsActive(activation, now);

    /// <summary>
    /// Which of <paramref name="moduleIds"/> are active for the ambient tenant — the same question as
    /// <see cref="IsActiveAsync"/>, asked once for several modules instead of once per module.
    /// </summary>
    /// <remarks>
    /// Here rather than at the call sites for this file's stated reason: the derived-active arithmetic
    /// (including the grace window) must have ONE implementation, and a caller that needs the set rather
    /// than a single answer would otherwise write the loop itself. Two already had.
    /// </remarks>
    public static async Task<HashSet<string>> ActiveIdsAsync(
        SimplArchiveDbContext dbContext,
        IReadOnlyCollection<string> moduleIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (moduleIds.Count == 0)
        {
            return [];
        }

        var activations = await dbContext.ModuleActivations
            .Where(a => moduleIds.Contains(a.ModuleId))
            .ToListAsync(cancellationToken);

        return activations
            .Where(a => ModuleActivationPolicy.IsActive(a, now))
            .Select(a => a.ModuleId)
            .ToHashSet(StringComparer.Ordinal);
    }
}

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
}

using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Groups;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// The three invariants about <see cref="Group"/>, enforced from <c>SaveChanges</c>.
/// </summary>
/// <remarks>
/// <para>
/// A group cannot contain itself, directly or transitively (ADR: Group cycle detection mechanism); a group's
/// parent must belong to the same tenant (ADR: Cross-tenant group parent enforcement); and sibling groups —
/// same tenant and parent, root-level groups sharing a null parent included — cannot share a name (ADR: Group
/// name uniqueness scope).
/// </para>
/// <para>
/// <b>Still the single enforcement point</b>, called from the context's <c>SaveChanges</c> overrides: every
/// write path goes through them whichever handler triggered it, so none of these can be bypassed the way a
/// per-handler check could. Moving them out of the context changed WHERE the code lives and nothing about when
/// it runs.
/// </para>
/// <para>
/// <b>Why it moved:</b> the context had reached 997 lines, one change from the 1000-line rule, so adding ABI
/// 1.1's projection hook to it would have needed an exception nobody should have to grant. Group invariants are
/// the most separable thing in there — they share no state with the document rules and read a different entity
/// — so they are what leaves. No behaviour changed; the group tests are what proves it.
/// </para>
/// </remarks>
internal static class GroupInvariants
{
    internal static async Task ValidateAsync(SimplArchiveDbContext db, CancellationToken cancellationToken)
    {
        var changedGroups = db.ChangeTracker.Entries<Group>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Select(e => e.Entity)
            .ToList();

        if (changedGroups.Count == 0)
        {
            return;
        }

        var trackedGroups = db.ChangeTracker.Entries<Group>().ToDictionary(e => e.Entity.Id, e => e.Entity);

        foreach (var group in changedGroups)
        {
            if (group.ParentGroupId.HasValue)
            {
                await DetectCycleAndCrossTenantParentAsync(db, group, trackedGroups, cancellationToken);
            }

            await EnsureUniqueSiblingNameAsync(db, group, trackedGroups.Values, cancellationToken);
        }
    }

    private static async Task DetectCycleAndCrossTenantParentAsync(
        SimplArchiveDbContext db,
        Group group, Dictionary<Guid, Group> trackedGroups, CancellationToken cancellationToken)
    {
        var visited = new HashSet<Guid> { group.Id };
        var currentId = group.ParentGroupId;

        while (currentId.HasValue)
        {
            if (!visited.Add(currentId.Value))
            {
                throw new InvalidOperationException(
                    $"Group '{group.Id}' cannot be its own ancestor — assigning parent '{group.ParentGroupId}' would create a cycle.");
            }

            Guid parentTenantId;
            Guid? parentId;

            if (trackedGroups.TryGetValue(currentId.Value, out var trackedParent))
            {
                parentTenantId = trackedParent.TenantId;
                parentId = trackedParent.ParentGroupId;
            }
            else
            {
                // Ignores the tenant query filter deliberately, so a cross-tenant parent is caught by
                // the explicit check below with a clear message, rather than failing opaquely because
                // the filtered query found no matching row.
                var parent = await db.Groups
                    .IgnoreQueryFilters()
                    .Where(g => g.Id == currentId.Value)
                    .Select(g => new { g.TenantId, g.ParentGroupId })
                    .SingleAsync(cancellationToken);
                parentTenantId = parent.TenantId;
                parentId = parent.ParentGroupId;
            }

            if (parentTenantId != group.TenantId)
            {
                throw new InvalidOperationException(
                    $"Group '{group.Id}' (tenant '{group.TenantId}') cannot have a parent belonging to a different tenant ('{parentTenantId}').");
            }

            currentId = parentId;
        }
    }

    private static async Task EnsureUniqueSiblingNameAsync(
        SimplArchiveDbContext db,
        Group group, IEnumerable<Group> trackedGroups, CancellationToken cancellationToken)
    {
        var conflictsWithinBatch = trackedGroups.Any(other =>
            other.Id != group.Id
            && other.TenantId == group.TenantId
            && other.ParentGroupId == group.ParentGroupId
            && other.Name == group.Name);

        if (conflictsWithinBatch)
        {
            throw new InvalidOperationException(
                $"Group '{group.Id}' cannot share the name '{group.Name}' with another group under the same parent.");
        }

        // Nullable-vs-nullable equality here (g.ParentGroupId == group.ParentGroupId) is translated by EF
        // Core as a null-safe comparison (true when both sides are null), unlike a raw SQL "=" operator —
        // this is exactly why this check lives here rather than in a database unique index, which would
        // treat every NULL ParentGroupId as distinct and silently miss root-level name collisions.
        var conflictsWithPersisted = await db.Groups
            .Where(g => g.Id != group.Id
                && g.TenantId == group.TenantId
                && g.ParentGroupId == group.ParentGroupId
                && g.Name == group.Name)
            .AnyAsync(cancellationToken);

        if (conflictsWithPersisted)
        {
            throw new InvalidOperationException(
                $"Group '{group.Id}' cannot share the name '{group.Name}' with another group under the same parent.");
        }
    }
}

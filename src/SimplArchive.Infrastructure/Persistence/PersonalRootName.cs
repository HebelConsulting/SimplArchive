using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Users;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Keeps a personal space's name equal to its owner's display name (ADR 0671) — renamed in the same
/// <c>SaveChanges</c> that renames the person, so the two can never drift apart. Called BEFORE the document
/// validators, so the rename it performs is validated like any other write rather than slipping past them.
/// </summary>
/// <remarks>
/// <para>
/// The alternative was to rename from the one place that writes <c>User.DisplayName</c> today. Here instead for
/// the same reason <see cref="PersonalRootOwner"/> is: this is DERIVED data, and derived data maintained at a
/// call site is derived data the next call site forgets. One write path is one write path until it is two.
/// </para>
/// <para>
/// Renaming the root changes its WebDAV path (<c>/SimplArchive/Anna Schmidt/…</c>), which breaks a saved
/// favourite pointing into that space. That cost was accepted when the naming was decided: an ambiguous
/// "Personal/My Addressbook" stops being merely untidy the day a collection can be subscribed to from
/// elsewhere. Subfolder names are untouched.
/// </para>
/// </remarks>
public static class PersonalRootName
{
    /// <summary>Renames the personal root of every user whose display name changed in this batch.</summary>
    public static async Task FollowDisplayNameAsync(SimplArchiveDbContext dbContext, CancellationToken cancellationToken)
    {
        var renamed = dbContext.ChangeTracker.Entries<User>()
            .Where(e => e.State == EntityState.Modified && e.Property(u => u.DisplayName).IsModified)
            .Select(e => e.Entity)
            .ToList();

        if (renamed.Count == 0)
        {
            return;
        }

        foreach (var user in renamed)
        {
            // IgnoreQueryFilters(["TenantFilter"]) because a rename can be saved by a context with no ambient
            // tenant (seeding, a background worker); the lookup is already keyed on the user's own id, which is
            // tenant-unique. The soft-delete filter stays ON — a personal root in the recycle bin should not be
            // renamed out from under a restore.
            var root = await dbContext.Documents
                .IgnoreQueryFilters(["TenantFilter"])
                .SingleOrDefaultAsync(d => d.PersonalOfUserId == user.Id, cancellationToken);

            if (root is null)
            {
                continue; // not provisioned yet — it will be created with the new name
            }

            var name = PersonalSpaceName.For(user);
            if (root.Name != name)
            {
                root.Name = name;
            }
        }
    }
}

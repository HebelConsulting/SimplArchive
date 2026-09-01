using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Renames pre-ADR-0671 personal spaces from the fixed "Personal" to their owner's name (#795).
/// </summary>
/// <remarks>
/// <para>
/// The owner-name was applied only at CREATION, deliberately — renaming a mounted volume's folder breaks the
/// mount. The consequence nobody carried forward: an installation upgraded across ADR 0671 keeps "Personal"
/// for the life of the deployment, while every fresh install (and the nightly-reset kiosk, which is where we
/// habitually look) shows the new name. Two schemes forever, invisible where we watch, permanent where we do
/// not — the stranded-tenant class (#574), and only a startup heal closes it.
/// </para>
/// <para>
/// The mounts the original decision protected are kept working the other way around: WebDavMiddleware accepts
/// the legacy "Personal" first segment as an ALIAS for the caller's own space — the same recipe as the
/// /webdav → /SimplArchive move (ADR 0509): the canonical name changes, the old one is served as an alias.
/// That alias was itself retired later (#794), so read this as the precedent, not as a live route.
/// </para>
/// <para>
/// One space per SaveChanges, so a defect on one row cannot abort every other user's heal. NOT because of name
/// collisions: personal spaces live in a per-user namespace (the partial unique index on TenantId +
/// PersonalOfUserId) and are exempt from the tenant-wide root sibling rule by design, so a repository named
/// like the owner is legal beside the healed space. The catch below is defensive, not a designed path.
/// </para>
/// </remarks>
public static class LegacyPersonalSpaceHealer
{
    public static async Task HealAsync(SimplArchiveDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        // Startup context: no ambient tenant, so the tenant filter would silently match nothing (the
        // TokenController lesson). The soft-delete filter stays — a deleted space has no mount to serve.
        var legacyIds = await db.Documents
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.PersonalOfUserId != null && d.Name == PersonalRepositoryProvisioner.LegacyPersonalRepositoryName)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var healed = 0;
        foreach (var id in legacyIds)
        {
            var space = await db.Documents.IgnoreQueryFilters(["TenantFilter"]).SingleAsync(d => d.Id == id, cancellationToken);
            var owner = await db.Users.IgnoreQueryFilters(["TenantFilter"])
                .Where(u => u.Id == space.PersonalOfUserId)
                .Select(u => new { u.DisplayName, u.Email })
                .SingleOrDefaultAsync(cancellationToken);
            if (owner is null)
            {
                logger.LogWarning("Personal space {DocumentId} has no owner row; leaving its legacy name", id);
                continue;
            }

            var name = PersonalSpaceName.For(owner.DisplayName, owner.Email);
            if (name == space.Name)
            {
                continue; // an owner literally named Personal — nothing to move
            }

            space.Name = name;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                healed++;
            }
            catch (InvalidOperationException e)
            {
                // Defensive: personal spaces are exempt from the root sibling-name rule (see the class doc), so
                // no DESIGNED invariant refuses this rename — but an invariant added later must degrade to a
                // named Warning here rather than abort every other user's heal.
                db.ChangeTracker.Clear();
                logger.LogWarning("Personal space {DocumentId} could not be renamed to {Name} ({Reason}); it keeps its legacy name", id, name, e.Message);
            }
        }

        if (healed > 0)
        {
            logger.LogInformation("Renamed {Count} pre-ADR-0671 personal space(s) to their owner's name (#795); the WebDAV alias keeps old mounts serving", healed);
        }
    }
}

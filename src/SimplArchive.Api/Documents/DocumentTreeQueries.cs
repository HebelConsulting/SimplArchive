using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Tree walks several Document controllers share, as extension methods on the DbContext — CLAUDE.md's
/// prescribed home for query composition a controller would otherwise carry (issue #466).
/// </summary>
public static class DocumentTreeQueries
{
    /// <summary>A folder's full display path, e.g. "Repositories / Contracts / 2026", by walking up ParentId.</summary>
    public static async Task<string> BuildFolderPathAsync(this SimplArchiveDbContext dbContext, Guid folderId, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        Guid? currentId = folderId;

        while (currentId is { } id)
        {
            var node = await dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Name, d.ParentId })
                .SingleAsync(cancellationToken);
            names.Add(node.Name);
            currentId = node.ParentId;
        }

        names.Reverse();
        return string.Join(" / ", names.Prepend("Repositories"));
    }

    /// <summary>
    /// The root plus every descendant, breadth-first — what a cascading delete freezes-checks and an export
    /// serializes. Ignores the soft-delete filter deliberately where its callers do not.
    /// </summary>
    public static async Task<List<Document>> CollectSubtreeAsync(this SimplArchiveDbContext dbContext, Guid rootId, Document root, CancellationToken cancellationToken)
    {
        var subtree = new List<Document> { root };
        var currentLevelIds = new List<Guid> { rootId };

        while (currentLevelIds.Count > 0)
        {
            var children = await dbContext.Documents
                .Where(d => d.ParentId != null && currentLevelIds.Contains(d.ParentId!.Value))
                .ToListAsync(cancellationToken);

            if (children.Count == 0)
            {
                break;
            }

            subtree.AddRange(children);
            currentLevelIds = children.Select(c => c.Id).ToList();
        }

        return subtree;
    }

    /// <summary>
    /// The ACL scope that governs a node: the nearest ancestor that BREAKS inheritance, else the root. Asked by
    /// both the ACL editor and the effective-access view, so it lives beside the other tree walks rather than
    /// being copied into each (ADR 0571's rule for query composition).
    /// </summary>
    // The ACL scope a currently-inheriting document draws its grants from: the nearest ancestor that itself
    // breaks inheritance, else the repository root (whose own grants are the ultimate fallback). Mirrors the
    // resolution in EffectiveRightsCalculator (ADR "Document ACL inheritance resolution") — one query per
    // ancestor level, walking up from the parent.
    public static async Task<Guid?> ResolveGoverningScopeAsync(this SimplArchiveDbContext dbContext, Guid? parentId, CancellationToken cancellationToken)
    {
        var currentId = parentId;
        Guid? rootId = null;

        while (currentId is { } id)
        {
            var node = await dbContext.Documents
                .Where(d => d.Id == id)
                .Select(d => new { d.Id, d.ParentId, d.BreaksInheritance })
                .SingleOrDefaultAsync(cancellationToken);

            if (node is null)
            {
                break;
            }

            if (node.BreaksInheritance)
            {
                return node.Id;
            }

            rootId = node.Id;
            currentId = node.ParentId;
        }

        return rootId;
    }
}

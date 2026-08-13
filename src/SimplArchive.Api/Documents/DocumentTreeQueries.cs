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
}

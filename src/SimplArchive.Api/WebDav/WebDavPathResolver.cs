using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.WebDav;

// A resolved WebDAV resource: the virtual repository-list root, a collection (folder/root Document), or a
// file (Document with a current version).
internal sealed class WebDavNode
{
    public Document? Document { get; init; }
    public bool IsRoot { get; init; }
    public bool IsCollection { get; init; }
    public string WebDavName { get; init; } = "";
    public string? ObjectKey { get; init; }
    public long Length { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }

    // The single mounted resource is named "SimplArchive" and its children mirror the Repositories tree-pane
    // exactly (ADR 0509): the Personal space, then the shared repositories the caller can see.
    public static WebDavNode Root() => new() { IsRoot = true, IsCollection = true, WebDavName = "SimplArchive", Created = DateTimeOffset.UnixEpoch, Modified = DateTimeOffset.UnixEpoch };

    public static WebDavNode Collection(Document document) => new()
    {
        Document = document,
        IsCollection = true,
        WebDavName = document.Name,
        Created = document.CreatedAt,
        Modified = document.CreatedAt,
    };

    // A special top-level folder (Intray / Check-out) — a collection not backed by a Document.
    public static WebDavNode Special(string name) => new()
    {
        IsCollection = true,
        WebDavName = name,
        Created = DateTimeOffset.UnixEpoch,
        Modified = DateTimeOffset.UnixEpoch,
    };
}

// WebDAV path segments → the Document tree, and back (issue #466 moved this out of the middleware). The
// mounted structure is exactly the Repositories tree-pane (ADR 0509), so this is where that mapping lives:
// roots, children (with the effective-rights filter), node resolution by WebDAV name (stem + current
// version's extension), and href emission under the canonical base path.
internal static class WebDavPathResolver
{
    // ---- Resolution: WebDAV path segments → a node --------------------------------------------------------
    internal static async Task<WebDavNode?> ResolveAsync(SimplArchiveDbContext db, User user, List<string> segments)
    {
        if (segments.Count == 0)
        {
            return WebDavNode.Root();
        }

        // First segment = a repository name (the user's Personal space, or a shared repository they can see).
        var roots = await RootsAsync(db, user);
        if (roots.FirstOrDefault(r => r.Name == segments[0]) is not { } repo)
        {
            return null;
        }

        var current = repo;
        for (var i = 1; i < segments.Count; i++)
        {
            var child = await ChildByWebDavNameAsync(db, current.Id, segments[i]);
            if (child is null)
            {
                return null;
            }

            current = child;
        }

        return await NodeForAsync(db, current);
    }

    internal static async Task<Document?> ChildByWebDavNameAsync(SimplArchiveDbContext db, Guid parentId, string webDavName)
    {
        // A folder's WebDAV name is its Name; a file's is Name + extension. Name (the stem) is unique per parent,
        // so match the folder name first, else the file stem.
        var byName = await db.Documents.SingleOrDefaultAsync(d => d.ParentId == parentId && d.Name == webDavName);
        if (byName is not null)
        {
            return byName;
        }

        var stem = Path.GetFileNameWithoutExtension(webDavName);
        return await db.Documents.SingleOrDefaultAsync(d => d.ParentId == parentId && d.Name == stem);
    }

    internal static async Task<List<Document>> RootsAsync(SimplArchiveDbContext db, User user)
    {
        // The user's Personal repository + shared repositories (root documents). ACL is enforced per operation;
        // the listing here is intentionally simple (the tenant filter already scopes it). The Personal repository
        // is ordered first so /webdav/Personal resolves to it even if a shared repository shares the name.
        var roots = await db.Documents
            .Where(d => d.ParentId == null && (d.PersonalOfUserId == null || d.PersonalOfUserId == user.Id))
            .ToListAsync();
        return roots.OrderByDescending(d => d.PersonalOfUserId == user.Id).ToList();
    }

    internal static async Task<List<WebDavNode>> ChildrenAsync(SimplArchiveDbContext db, User user, WebDavNode node, IEffectiveRightsCalculator calc)
    {
        if (node.IsRoot)
        {
            // The root lists the repositories the caller can see (ADR "WebDAV hardening"): the Personal repo is
            // always the user's own; shared roots are CanSee-filtered. Intray / Check-out live under Personal now.
            var visible = new List<WebDavNode>();
            foreach (var root in await RootsAsync(db, user))
            {
                if (root.PersonalOfUserId == user.Id || (await calc.GetEffectiveRightsAsync(user.Id, root.Id)).CanSee)
                {
                    visible.Add(WebDavNode.Collection(root));
                }
            }

            return visible;
        }

        var children = await db.Documents.Where(d => d.ParentId == node.Document!.Id).ToListAsync();
        var result = new List<WebDavNode>();

        // The Personal repository holds the two virtual special folders, which shadow any real same-named child.
        if (node.Document!.PersonalOfUserId == user.Id)
        {
            result.Add(WebDavNode.Special(WebDavMiddleware.IntrayName));   // the per-user Intray staging folder
            result.Add(WebDavNode.Special(WebDavMiddleware.CheckoutName)); // the caller's checked-out documents
            children = children.Where(c => c.Name is not (WebDavMiddleware.IntrayName or WebDavMiddleware.CheckoutName)).ToList();
        }

        // ACL-filter each child by CanSee (ADR "WebDAV hardening").
        foreach (var child in children)
        {
            if ((await calc.GetEffectiveRightsAsync(user.Id, child.Id)).CanSee)
            {
                result.Add(await NodeForAsync(db, child));
            }
        }

        return result;
    }

    internal static async Task<WebDavNode> NodeForAsync(SimplArchiveDbContext db, Document document)
    {
        // The document's current version honoring the CurrentVersionId pointer (issue #265), else latest confirmed.
        var version = await CurrentVersion.ResolveAsync(db.DocumentVersions, document.Id, document.CurrentVersionId);

        if (version is null)
        {
            return WebDavNode.Collection(document); // no version → a folder
        }

        var extension = Path.GetExtension(version.ObjectKey);
        return new WebDavNode
        {
            Document = document,
            IsCollection = false,
            WebDavName = document.Name + extension,
            ObjectKey = version.ObjectKey,
            Length = version.SizeBytes ?? 0,
            ContentType = ContentTypes.ForExtension(extension),
            Created = document.CreatedAt,
            Modified = version.CreatedAt,
        };
    }

    internal static async Task<List<Document>> CollectSubtreeAsync(SimplArchiveDbContext db, Guid rootId)
    {
        var subtree = new List<Document>();
        var level = new List<Guid> { rootId };
        subtree.Add(await db.Documents.SingleAsync(d => d.Id == rootId));
        while (level.Count > 0)
        {
            var children = await db.Documents.Where(d => d.ParentId != null && level.Contains(d.ParentId!.Value)).ToListAsync();
            if (children.Count == 0)
            {
                break;
            }

            subtree.AddRange(children);
            level = children.Select(c => c.Id).ToList();
        }

        return subtree;
    }

    /// <summary>
    /// The href for <paramref name="segments"/>, rooted at the prefix the REQUEST arrived on.
    /// </summary>
    /// <remarks>
    /// Not the canonical <c>/SimplArchive</c> constant, which is what ADR 0509 originally specified and what
    /// ADR 0645 supersedes: a Depth-1 PROPFIND on the legacy <c>/webdav</c> answered with members at
    /// <c>/SimplArchive/…</c> — hrefs outside the collection the client had asked about (RFC 4918 §9.1). A
    /// client cannot place those beside the mount, so it drew them UNDER it, and the repositories appeared
    /// cascaded inside the personal space on an already-saved mount.
    /// </remarks>
    internal static string HrefFor(string basePath, List<string> segments) =>
        basePath + string.Concat(segments.Select(s => "/" + Uri.EscapeDataString(s)));

}

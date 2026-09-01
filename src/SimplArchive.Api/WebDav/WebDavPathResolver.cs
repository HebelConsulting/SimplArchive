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
    public string WebDavName { get; init; } = string.Empty;
    public string? ObjectKey { get; init; }
    public long Length { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }

    /// <summary>Set when this node was reached through a REFERENCE filed in the folder (#769).</summary>
    /// <remarks>
    /// The Document is the TARGET either way — that is what a reference is — so reading and writing act on the
    /// document itself, which is what makes a reference useful. DELETE is the exception and the reason this is
    /// carried at all: it must remove the appearance, never the document.
    /// </remarks>
    public Guid? ViaReferenceId { get; init; }

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
        Guid? viaReference = null;
        for (var i = 1; i < segments.Count; i++)
        {
            viaReference = null;
            var child = await ChildByWebDavNameAsync(db, current.Id, segments[i]);
            if (child is null)
            {
                // Not a real child — but it may be a document REFERENCED into this folder (#769), which the
                // listing now shows and which must therefore also be addressable. A real child is looked up
                // first, so a name clash resolves to the child, exactly as the listing shows it.
                if (await ReferencedDocumentByWebDavNameAsync(db, current.Id, segments[i]) is not { } referenced)
                {
                    return null;
                }

                viaReference = referenced.Reference.Id;
                child = referenced.Target;
            }

            current = child;
        }

        var node = await NodeForAsync(db, current);
        return viaReference is null ? node : new WebDavNode
        {
            Document = node.Document,
            IsCollection = node.IsCollection,
            WebDavName = node.WebDavName,
            ObjectKey = node.ObjectKey,
            Length = node.Length,
            ContentType = node.ContentType,
            Created = node.Created,
            Modified = node.Modified,
            ViaReferenceId = viaReference,
        };
    }

    /// <summary>The child of <paramref name="parentId"/> whose WebDAV name IS <paramref name="webDavName"/>.</summary>
    /// <remarks>
    /// A folder's WebDAV name is its Name; a file's is Name + the current version's extension. Both candidates
    /// are looked up by Name (unique per parent) and then CONFIRMED against the name the mount actually shows —
    /// which is the whole point, and what the earlier two-step lookup skipped.
    ///
    /// It matched <c>d.Name == webDavName</c> first, for folders, and a FILE's Name is its stem — so a document
    /// called <c>Testing My Test</c> answered at the extension-less path <c>…/Testing My Test</c> as well as at
    /// its real <c>…/Testing My Test.docx</c>. Measured (#794): a 207 whose <c>href</c> and <c>displayname</c>
    /// disagreed, which is already wrong under RFC 4918, and worse in context — an editor probes exactly that
    /// name to find out whether it is FREE, and was told the name was taken by a resource that does not have it.
    /// </remarks>
    internal static async Task<Document?> ChildByWebDavNameAsync(SimplArchiveDbContext db, Guid parentId, string webDavName)
    {
        var stem = Path.GetFileNameWithoutExtension(webDavName);
        var candidates = await db.Documents
            .Where(d => d.ParentId == parentId && (d.Name == webDavName || d.Name == stem))
            .ToListAsync();

        foreach (var candidate in candidates)
        {
            if (string.Equals((await NodeForAsync(db, candidate)).WebDavName, webDavName, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    internal static async Task<List<Document>> RootsAsync(SimplArchiveDbContext db, User user)
    {
        // The user's Personal repository + shared repositories (root documents). ACL is enforced per operation;
        // the listing here is intentionally simple (the tenant filter already scopes it). The Personal repository
        // is ordered first so /SimplArchive/Personal resolves to it even if a shared repository shares the name.
        var roots = await db.Documents
            .Where(d => d.ParentId == null && (d.PersonalOfUserId == null || d.PersonalOfUserId == user.Id))
            .ToListAsync();
        return roots.OrderByDescending(d => d.PersonalOfUserId == user.Id).ToList();
    }

    internal static async Task<List<WebDavNode>> ChildrenAsync(
        SimplArchiveDbContext db, User user, WebDavNode node, IEffectiveRightsCalculator calc, ILogger? logger = null)
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

        // …and the DOCUMENTS referenced into this folder (#769). A reference is another appearance of a
        // document, and ADR 0509 binds this mount to the Repositories tree — which shows them. Without this the
        // same archive presented two shapes: the workbench listed a referenced invoice in the folder its owner
        // filed it into, and the mounted drive listed that folder without it.
        // The folder's REAL ancestor chain, for the cycle guard below (#615) — computed once per listing,
        // and only when a reference exists to check against.
        HashSet<Guid>? ancestors = null;
        foreach (var (reference, target) in await ReferencedDocumentsAsync(db, node.Document!.Id))
        {
            // A reference pointing back UP its own real path would let a file browser walk
            // Personal/A/RefToPersonal/A/… forever. Omitted, and SAID (ADR 0626) — the same rule IMAP's
            // catalog applies, on the same reasoning: silence here is indistinguishable from "never filed".
            ancestors ??= await AncestorsOfAsync(db, node.Document!.Id);
            if (target.Id == node.Document!.Id || ancestors.Contains(target.Id))
            {
                logger?.LogWarning(
                    "WebDAV: reference to {TargetName} in folder {FolderId} points back into its own path and "
                    + "is OMITTED from the mount; the app shows it and the mounted drive cannot. Set "
                    + "Serilog:MinimumLevel:Override:SimplArchive.Api.WebDav to Trace for the walk",
                    target.Name, node.Document!.Id);
                continue;
            }

            // Its rights are its OWN: the target lives elsewhere and inherits from its real parent, not from
            // the folder it is referenced into. A reference the caller may not follow is simply not there,
            // rather than there and failing on access.
            if (!(await calc.GetEffectiveRightsAsync(user.Id, target.Id)).CanSee)
            {
                continue;
            }

            var projected = await NodeForAsync(db, target);

            // One wire name can only mean one thing. A real child wins and the reference is dropped — the same
            // rule IMAP applies to a clashing referenced folder — because the alternative is two entries a
            // client cannot tell apart, and a save-by-name that lands on whichever the server picked.
            if (result.Any(existing => string.Equals(existing.WebDavName, projected.WebDavName, StringComparison.OrdinalIgnoreCase)))
            {
                logger?.LogWarning(
                    "WebDAV: {Name} in folder {FolderId} is claimed by both a child and a reference; the "
                    + "reference is not listed, so it is invisible on the mounted drive while the app shows it",
                    projected.WebDavName, node.Document!.Id);
                continue;
            }

            result.Add(new WebDavNode
            {
                Document = projected.Document,
                IsCollection = projected.IsCollection,
                WebDavName = projected.WebDavName,
                ObjectKey = projected.ObjectKey,
                Length = projected.Length,
                ContentType = projected.ContentType,
                Created = projected.Created,
                Modified = projected.Modified,
                ViaReferenceId = reference.Id,
            });
        }

        return result;
    }

    /// <summary>One referenced document in a folder, matched by its WebDAV name (stem + extension).</summary>
    internal static async Task<(DocumentReference Reference, Document Target)?> ReferencedDocumentByWebDavNameAsync(
        SimplArchiveDbContext db, Guid folderId, string webDavName)
    {
        foreach (var candidate in await ReferencedDocumentsAsync(db, folderId))
        {
            var node = await NodeForAsync(db, candidate.Target);
            if (string.Equals(node.WebDavName, webDavName, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Everything referenced into a folder — documents AND folders (#615), paired with the reference.</summary>
    /// <remarks>
    /// Folders included since #615: resolution now walks INTO a referenced folder (deeper path segments hit
    /// the target's real children), so the old reason for excluding them — a folder appearing without its
    /// subtree — no longer holds. The projection itself decides folder-vs-file exactly as it does for a real
    /// child: <see cref="NodeForAsync"/>, by whether a confirmed version exists.
    /// </remarks>
    internal static async Task<List<(DocumentReference Reference, Document Target)>> ReferencedDocumentsAsync(
        SimplArchiveDbContext db, Guid folderId)
    {
        var references = await db.DocumentReferences.Where(r => r.ParentFolderId == folderId).ToListAsync();
        if (references.Count == 0)
        {
            return [];
        }

        var targetIds = references.Select(r => r.TargetDocumentId).ToList();
        var targets = (await db.Documents
                .Where(d => targetIds.Contains(d.Id))
                .ToListAsync())
            .ToDictionary(d => d.Id);

        return [.. references
            .Where(r => targets.ContainsKey(r.TargetDocumentId))
            .Select(r => (r, targets[r.TargetDocumentId]))];
    }

    /// <summary>The ids on a folder's real ParentId chain, root included. Bounded by the cycle invariant.</summary>
    private static async Task<HashSet<Guid>> AncestorsOfAsync(SimplArchiveDbContext db, Guid folderId)
    {
        var ancestors = new HashSet<Guid>();
        var cursor = await db.Documents.Where(d => d.Id == folderId).Select(d => d.ParentId).FirstOrDefaultAsync();
        while (cursor is { } id && ancestors.Add(id))
        {
            cursor = await db.Documents.Where(d => d.Id == id).Select(d => d.ParentId).FirstOrDefaultAsync();
        }

        return ancestors;
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

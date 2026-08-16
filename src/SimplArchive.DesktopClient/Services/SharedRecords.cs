using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

// The wire shapes several areas share (#443 finale): promoted to namespace level so no area client owns
// them and nothing qualifies across owners. Node is THE listing row every tree/list surface holds.



/// <summary>A row that carries the addresses its listing advertised (ADR 0543/0555).</summary>
public interface IAdvertisesLinks
{
    string Name { get; }

    string? Href(string rel);
}

// ---- Document ACL / Manage access (ADR "Manage-access UI for document/folder ACLs") -------------

public sealed record AclRights(
    bool CanSee, bool CanReadContent, bool CanEditContent, bool CanEditIndexData,
    bool CanCreateSubItems, bool CanDelete, bool CanMove, bool CanAnnotate, bool CanManagePermissions);


public sealed record Node(Guid Id, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold = false, bool CheckedOut = false, bool CheckedOutByMe = false, string CheckedOutByName = "",
    string DocumentType = "", DateOnly? DocumentDate = null, long? SizeBytes = null, IReadOnlyList<string>? Tags = null, string SensitivityLabelName = "", string? SensitivityLabelColor = null, int VersionCount = 0,
    // The latest confirmed version's CreatedAt (filing timestamp) — the "Created" folder contents-sort key
    // (ADR "Per-folder contents sort order"). Null for a folder / version-less doc.
    DateTimeOffset? VersionCreatedAt = null,
    // The item's own sub-resource addresses, as the listing advertised them (ADR 0543, issue #416): a client
    // holding a row follows these instead of composing a path from the document id from a template. Empty only if
    // the row came from somewhere that does not advertise them, in which case a caller must fetch the
    // resource — never rebuild the path.
    IReadOnlyDictionary<string, string>? Links = null)
{
    /// <summary>The advertised href for <paramref name="rel"/>.</summary>
    /// <remarks>
    /// Throws rather than falling back to a composed path. A rel the server did not advertise means the
    /// action is not available here (ADR 0543) or the row came from a listing that does not advertise it —
    /// either way, rebuilding the URL would paper over the very thing this is replacing, and would do it
    /// silently.
    /// </remarks>
    public string Href(string rel) =>
        Links is not null && Links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException(
                $"The '{rel}' rel was not advertised for '{Name}'. Follow a rel the resource offers, or fetch "
                + "the resource — do not compose the URL (ADR 0543).");
}

// AuthorCardHref: the "author-card" rel as the server advertised it, or null for a ServiceAccount author
// (ADR 0543/0544).
public sealed record Comment(Guid Id, Guid? ParentMessageId, string Body, string AuthorName, DateTimeOffset CreatedAt, string? AuthorCardHref,
    int Kind, int? VersionNumber, string? VersionComment, int? VersionCommentKind,
    // The names behind the body's "@[id]" tokens, resolved by the server (issue #383). The body stores ids,
    // never names, so a rename cannot break a mention.
    IReadOnlyList<Mention> Mentions);

public sealed record Mention(Guid UserId, string DisplayName);

// Bulk actions over a set of selected documents (ADR "Bulk actions on selected documents") — each POSTs
// { ids, ... } and returns how many items succeeded vs were skipped (an item the caller can't touch or
// that's refused is skipped, not an error).
public sealed record BulkResult(int Succeeded, int Skipped);

// Both rows carry the address the WRITE goes to — an existing entry advertises `edit`/`remove`, a principal
// you may newly grant to advertises `grant`. Same shape, so the write is expressed once (ADR 0543/0555).
public sealed record AclEntryInfo(string PrincipalType, Guid PrincipalId, AclRights Rights,
    IReadOnlyDictionary<string, string>? Links = null) : IAdvertisesLinks
{
    public string Name => $"{PrincipalType}/{PrincipalId}";

    public string? Href(string rel) => Links is not null && Links.TryGetValue(rel, out var href) ? href : null;
}

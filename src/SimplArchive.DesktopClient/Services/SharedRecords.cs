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


/// <summary>One kind of child a folder will accept, exactly as the server described it (#673).</summary>
/// <param name="MaskId">The mask a child created this way will wear.</param>
/// <param name="Name">The mask's name on its current version — the menu label, so a rename follows.</param>
/// <param name="Folder">Whether creating this makes a folder, which picks the icon.</param>
/// <param name="Href">Where to POST. Advertised, never composed — nothing is appended to it (ADR 0543).</param>
/// <param name="FolderMask">
/// The <c>folderMask</c> body value to send back, or null when the address alone says what to make. Handed
/// over by the server and returned unread: the vocabulary stays the server's, so no client keeps a copy.
/// </param>
public sealed record CreatableChild(Guid MaskId, string Name, bool Folder, string Href, string? FolderMask, string Prompt, string? Icon = null);

public sealed record Node(Guid Id, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders, bool HasReferences, bool OnLegalHold = false, bool CheckedOut = false, bool CheckedOutByMe = false, string CheckedOutByName = "",
    string DocumentType = "", DateOnly? DocumentDate = null, long? SizeBytes = null, IReadOnlyList<string>? Tags = null, string SensitivityLabelName = "", string? SensitivityLabelColor = null, int VersionCount = 0,
    // Who filed the current version, falling back to who created the document (#768) — the owner column.
    string CreatedBy = "",
    // The latest confirmed version's CreatedAt (filing timestamp) — the "Created" folder contents-sort key
    // (ADR "Per-folder contents sort order"). Null for a folder / version-less doc.
    DateTimeOffset? VersionCreatedAt = null,
    // The item's own sub-resource addresses, as the listing advertised them (ADR 0543, issue #416): a client
    // holding a row follows these instead of composing a path from the document id from a template. Empty only if
    // the row came from somewhere that does not advertise them, in which case a caller must fetch the
    // resource — never rebuild the path.
    IReadOnlyDictionary<string, string>? Links = null,
    // The kinds of child this folder will accept, each with the address that creates one (#673). Supplied by
    // the listing, so a context menu is built from it without a round trip — and a mask nobody hardcoded still
    // gets an entry, because the client never maps a mask to an endpoint.
    IReadOnlyList<CreatableChild>? Admits = null,
    // What this row is DRAWN as — the mask's icon token, or null to keep the generic glyph. A token rather
    // than an MDI name, because the web client draws from a different set entirely.
    string? Icon = null,
    // What the SERVER says this caller may do to this row (#858): the Delete, Rename/Sort-order and Move-to
    // gates. Flags rather than rels because DELETE and PUT live at the row's own address (ADR 0719), and false
    // by default because absence means "not available to you, here, now" (ADR 0543) — the safe direction.
    bool CanDelete = false,
    bool CanEditIndexData = false,
    bool CanMove = false,
    bool CanManagePermissions = false,
    // May a PLAIN child — a folder, or an uploaded document — be created in this row? The successor to the
    // `create-child` rel, which addressed the same URL as `children` and differed only by method (#854,
    // ADR 0719). The rel answered the mask rule only; this answers the rights too.
    bool CanCreateChildren = false)
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

// FileExtension is the current version's derived extension (ADR "Extension off Document.Name"); native
// Open/Save-as append it to Document.Name (the bare stem) to reconstruct a correct filename.
public sealed record Preview(string? PreviewUrl, bool PreviewConverted, string? DownloadUrl, string? TextLayoutUrl, string? PreviewPagesUrl, string FileExtension, string? AnnotationsUrl = null);
// A user option for the reviewer picker.
// RemoveHref is set only where the option came from a collection whose rows advertise a removal address —
// a group's members; it is null for pickers such as reminder targets (issue #416).
public sealed record UserOptionInfo(Guid Id, string DisplayName, string? RemoveHref = null);
// The two sides of a comparison as extracted texts (ADR 0712) — the client computes the side-by-side rows
// from them via the shared SimplArchive.Presentation.TextDiff. Serves both the version compare and the
// check-out working-copy compare (ADR 0517). Available=false when a side has no extractable text (or, for
// check-out, no working-copy stash exists).
public sealed record VersionComparison(bool Available, string FromText, string ToText)
{
    // The one wire parse both consumers (VersionsClient, CheckoutClient) share — the payloads are the same shape.
    public static VersionComparison Parse(System.Text.Json.JsonElement json) => new(
        json.TryGetProperty("available", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.True,
        json.TryGetProperty("fromText", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.String ? f.GetString()! : string.Empty,
        json.TryGetProperty("toText", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString()! : string.Empty);
}

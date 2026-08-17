namespace SimplArchive.Client.Models;

/// <summary>
/// One node of the archive as a listing described it — a repository, a folder, a document, a reference, or one
/// of the synthetic tree nodes (the Administration branch, the Personal-space groupings).
/// </summary>
/// <remarks>
/// Public and shared for the same reason as <see cref="Hypermedia.LinkResponse"/>: it is referenced 73 times
/// and by most of the workbench, so decomposing the page into components (ADR 0558) would otherwise need a
/// private copy of it per component.
/// </remarks>
public record BrowseNode(Guid Id, string Name, bool HasChildren, bool HasVersions, bool HasSubfolders,
    bool HasReferences = false, bool IsReference = false, Guid ReferenceId = default, Guid? RealParentId = null,
    Guid RepositoryId = default, string FileExtension = "", bool OnLegalHold = false,
    bool CheckedOut = false, bool CheckedOutByMe = false, string CheckedOutByName = "",
    // Synthetic tenant-admin tree nodes (ADR "Tenant-admin Administration → Users view"): "admin-root" and
    // "admin-users" load their children from the admin endpoint, not GET /documents/{id}/children.
    string AdminKind = "",
    // Synthetic Personal-space tree nodes (ADR "GUI-tree Personal space grouping"): "personal-root" (the
    // Personal repository, which also injects Intray/Check-out children) and the leaf launchers "intray" /
    // "checkout" (clicking them switches to the corresponding bottom tab, mirroring /webdav/Personal).
    string PersonalKind = "",
    // List-row columns (ADR "List-row columns and sorting").
    string DocumentType = "", DateOnly? DocumentDate = null, long? SizeBytes = null, IReadOnlyList<string>? Tags = null,
    // The data-classification sensitivity label (ADR "Configurable sensitivity labels + upload defaults") —
    // the label name + colour for the list-row badge; empty name = None (no badge).
    string? SensitivityLabelName = null, string? SensitivityLabelColor = null,
    // Confirmed-version count (ADR "Versions dialog") — gates the row's "Versions…" menu item (> 1).
    int VersionCount = 0,
    // The latest confirmed version's CreatedAt (filing timestamp) — the "Created" contents-sort key (ADR
    // "Per-folder contents sort order"). Null for a folder / version-less doc.
    DateTimeOffset? VersionCreatedAt = null,
    // The thread's URL as the SERVER advertised it (the "chat" rel), not one this client composed — see
    // ADR 0543. Null for a synthetic tree node, and for a node from a list that doesn't emit the rel yet;
    // those fall back to a composed URL, which is what the hypermedia guard's allowlist is burning down.
    string? ChatHref = null,
    // The row's own sub-resource addresses, as the listing advertised them (ADR 0543, issue #416) — a client
    // holding a row follows these instead of rebuilding a path from its id. Null for a SYNTHETIC node (the
    // Administration branch, the Personal-space groupings), which stands for no server resource, and for a
    // row from a listing that does not advertise them; a caller with neither must FETCH the resource and
    // follow its rel, never compose.
    IReadOnlyDictionary<string, string>? Links = null)
{
    public bool IsFolder => !HasVersions;

    // An EMPTY folder — no subfolders and no documents (ADR "Empty-folder tree icon", issue #352). The tree
    // tints its glyph so you can see at a glance which folders hold nothing, without expanding them.
    // HasChildren (any child) is the right flag, not HasSubfolders (which only drives the expander caret): a
    // folder holding only documents is a tree leaf but is NOT empty. The pseudo-nodes are excluded — the
    // Administration branch and the Intray / Check-out launchers aren't folders, and the Personal root always
    // holds those launchers.
    public bool IsEmptyFolder => IsFolder && !HasChildren && AdminKind == "" && PersonalKind == "";

    // Which glyph colour this node takes in the tree (ADR "Folder icon scheme"), as the CSS class that
    // carries it. Gold is for containers only, so the Intray / Check-out launchers and the Administration
    // branch go muted — the Personal ROOT is a container and stays gold.
    public string TreeGlyphClass =>
        IsEmptyFolder ? "wb-tree-empty"
        : AdminKind != "" || PersonalKind is "intray" or "checkout" ? "wb-tree-muted"
        : "wb-tree-folder";

    // A checked-out document is shown as "[holder] name" (ADR "Check-out working-copy stash").
    public string DisplayName => CheckedOut && !string.IsNullOrEmpty(CheckedOutByName) ? $"[{CheckedOutByName}] {Name}" : Name;

    public bool CheckedOutByOther => CheckedOut && !CheckedOutByMe;

    public IReadOnlyList<string> TagList => Tags ?? [];
}

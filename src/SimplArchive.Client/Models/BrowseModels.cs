using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>Mirrors the server's <c>FolderContentsSortOrder</c> (ADR "Per-folder contents sort order").</summary>
public enum FolderContentsSortOrder { Name = 0, DocumentDate = 1, Created = 2 }

/// <summary>One page of a folder's children, plus the order the folder is configured to list them in.</summary>
public record DocumentChildrenResponse
{
    public List<DocumentSummary> Children { get; set; } = [];

    /// <summary>
    /// The folder's persisted contents order, riding in the envelope so opening a folder does not cost a second
    /// request to learn it (ADR 0557).
    /// </summary>
    public FolderContentsSortOrder ContentsSortOrder { get; set; }

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>A child row as a listing described it — the shape <see cref="BrowseNode"/> is built from.</summary>
public record DocumentSummary
{
    // What the server says this caller may do to this row (#858) — the Delete / Rename / Move gates.
    public bool CanDelete { get; set; }

    public bool CanEditIndexData { get; set; }

    public bool CanMove { get; set; }

    public bool CanManagePermissions { get; set; }

    /// <summary>May the caller create a plain child here? Replaces the `create-child` rel (#854).</summary>
    public bool CanCreateChildren { get; set; }

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool HasChildren { get; set; }
    public bool HasVersions { get; set; }
    public bool HasSubfolders { get; set; }
    public bool HasReferences { get; set; }
    public bool OnLegalHold { get; set; }
    public bool CheckedOut { get; set; }
    public bool CheckedOutByMe { get; set; }
    public string CheckedOutByName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public DateOnly? DocumentDate { get; set; }
    public long? SizeBytes { get; set; }
    public List<string> Tags { get; set; } = [];
    public Guid? SensitivityLabelId { get; set; }
    public string SensitivityLabelName { get; set; } = string.Empty;
    public string? SensitivityLabelColor { get; set; }
    public int VersionCount { get; set; }
    public DateTimeOffset? VersionCreatedAt { get; set; }
    /// <summary>What this folder will accept, with the address for each (#673). Empty for a non-folder.</summary>
    public List<CreatableChild> Admits { get; set; } = [];

    /// <summary>The mask's icon token, or null for the generic glyph.</summary>
    public string? Icon { get; set; }

    /// <summary>Who filed the current version, falling back to who created the document (#768).</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>The references (shortcuts) filed in a folder — extra rows alongside its real children.</summary>
public record ReferenceListResponse
{
    public List<ReferenceSummary> References { get; set; } = [];
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>
/// A reference row. <see cref="Id"/> is the TARGET's id, so opening or acting on it acts on the target;
/// <see cref="ReferenceId"/> identifies the shortcut itself (what an "unplace" removes).
/// </summary>
public record ReferenceSummary
{
    public Guid ReferenceId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool HasChildren { get; set; }
    public bool HasVersions { get; set; }
    public bool HasSubfolders { get; set; }
    public bool HasReferences { get; set; }
    public Guid? RealParentId { get; set; }

    // The TARGET's list-row columns, exactly as a child row carries them (#768) — a reference is another
    // appearance of a document, so its row is the same row.
    public string FileExtension { get; set; } = string.Empty;
    public string DocumentType { get; set; } = string.Empty;
    public DateOnly? DocumentDate { get; set; }
    public long? SizeBytes { get; set; }
    public List<string> Tags { get; set; } = [];
    public string SensitivityLabelName { get; set; } = string.Empty;
    public string? SensitivityLabelColor { get; set; }
    public int VersionCount { get; set; }
    public DateTimeOffset? VersionCreatedAt { get; set; }
    public string? Icon { get; set; }

    /// <inheritdoc cref="DocumentSummary.CreatedBy"/>
    public string CreatedBy { get; set; } = string.Empty;

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>The current user's personal repository (ADR "Per-user personal repository").</summary>
public record PersonalRepositoryResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool HasChildren { get; set; }
    public bool HasSubfolders { get; set; }

    /// <summary>Its own advertised addresses — `children` above all, which is how a picker walks into it
    /// without composing a path from the id beside it (ADR 0543).</summary>
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>
/// A document fetched by id — the "I hold an id, not a resource" case (ADR 0543). Links is what the fetch is
/// usually for; Name rides along so a caller that only wants the display name goes through the same single
/// sanctioned fetch rather than keeping a private response type for the same GET.
/// </summary>
public sealed class DocumentLinksResponse
{
    public string Name { get; set; } = string.Empty;

    // The capability flags the document resource carries (#858). Read HERE because the Go to / search-hit path
    // builds its tree node from this response rather than from a listing row — the one surface where forgetting
    // them would make the same folder behave differently depending on how the user arrived at it.
    public bool CanDelete { get; set; }

    public bool CanEditIndexData { get; set; }

    public bool CanManagePermissions { get; set; }

    /// <summary>May the caller create a plain child here? Replaces the `create-child` rel (#854).</summary>
    public bool CanCreateChildren { get; set; }

    public List<LinkResponse>? Links { get; set; }
}

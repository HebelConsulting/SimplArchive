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
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool HasChildren { get; set; }
    public bool HasVersions { get; set; }
    public bool HasSubfolders { get; set; }
    public bool HasReferences { get; set; }
    public bool OnLegalHold { get; set; }
    public bool CheckedOut { get; set; }
    public bool CheckedOutByMe { get; set; }
    public string CheckedOutByName { get; set; } = "";
    public string FileExtension { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public DateOnly? DocumentDate { get; set; }
    public long? SizeBytes { get; set; }
    public List<string> Tags { get; set; } = [];
    public Guid? SensitivityLabelId { get; set; }
    public string SensitivityLabelName { get; set; } = "";
    public string? SensitivityLabelColor { get; set; }
    public int VersionCount { get; set; }
    public DateTimeOffset? VersionCreatedAt { get; set; }
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
    public string Name { get; set; } = "";
    public bool HasChildren { get; set; }
    public bool HasVersions { get; set; }
    public bool HasSubfolders { get; set; }
    public bool HasReferences { get; set; }
    public Guid? RealParentId { get; set; }
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>The current user's personal repository (ADR "Per-user personal repository").</summary>
public record PersonalRepositoryResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool HasChildren { get; set; }
    public bool HasSubfolders { get; set; }
}

/// <summary>
/// A document fetched by id — the "I hold an id, not a resource" case (ADR 0543). Links is what the fetch is
/// usually for; Name rides along so a caller that only wants the display name goes through the same single
/// sanctioned fetch rather than keeping a private response type for the same GET.
/// </summary>
public sealed class DocumentLinksResponse
{
    public string Name { get; set; } = string.Empty;

    public List<LinkResponse>? Links { get; set; }
}

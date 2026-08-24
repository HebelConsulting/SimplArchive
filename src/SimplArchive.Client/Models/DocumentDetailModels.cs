using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>
/// The shapes a document's detail is read in — mask, index data, versions, chat, text layout.
/// </summary>
/// <remarks>
/// Shared because two panes show a document's detail: the Repositories pane, and the Recycle bin's own
/// deliberately isolated one. Isolating the STATE is the recycle bin's requirement; duplicating the shapes it
/// reads would be a different thing, and the kind that drifts (ADR 0558).
/// </remarks>
public record MaskResponse
{
    public Guid? MaskId { get; set; }

    public string? Name { get; set; }

    public int? VersionNumber { get; set; }

    /// <summary>This assignment's addresses — its <c>definition</c> is where the mask's field definitions
    /// live, which is what the index editor needs before it can offer a box (#729).</summary>
    public List<LinkResponse> Links { get; set; } = [];
}

public record IndexDataResponse
{
    public List<FieldGroup> Fields { get; set; } = [];
}

public record VersionListResponse
{
    public List<VersionResponse> Versions { get; set; } = [];

    public Guid? CurrentVersionId { get; set; }

    public int? CurrentVersionNumber { get; set; }

    /// <summary>
    /// The current version: the server's CurrentVersionId pointer when set (ADR "Version-restore via a
    /// current-version pointer", issue #265), else the latest confirmed for an unpinned document.
    /// </summary>
    public static VersionResponse? PickCurrent(List<VersionResponse> confirmed, Guid? currentVersionId) =>
        confirmed.FirstOrDefault(v => v.Id == currentVersionId)
        ?? confirmed.OrderByDescending(v => v.VersionNumber ?? 0).FirstOrDefault();
}

public record VersionResponse
{
    /// <summary>Draft/InReview/Approved/Rejected/Released — or null when no workflow was ever started;
    /// what labels the workflow affordance by state without following the rel (review round).</summary>
    public string? WorkflowStatus { get; set; }

    public Guid Id { get; set; }
    public int? VersionNumber { get; set; }
    public string Status { get; set; } = "";
    public bool PreviewConverted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
    public string? DocumentDate { get; set; }
    public string? OcrLanguages { get; set; }
    public string? FileExtension { get; set; }
    public string? ObjectKey { get; set; }
    public List<LinkResponse> Links { get; set; } = [];
}

public record ChatMessageListResponse
{
    public List<ChatMessageResponse> Messages { get; set; } = [];

    public List<LinkResponse> Links { get; set; } = [];
}

public record TextLayoutResponse
{
    public List<TextLayoutPage> Pages { get; set; } = [];
}

public record TextLayoutPage
{
    public List<TextLayoutWord> Words { get; set; } = [];
}

public record TextLayoutWord
{
    public string Text { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }
}

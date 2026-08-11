using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>One index-data field and its values, as the detail pane displays them.</summary>
/// <remarks>
/// Shared: the Repositories detail pane and the Recycle bin's own isolated detail pane show the same thing
/// about a document, so they read one shape rather than two (ADR 0558).
/// </remarks>
public record FieldGroup
{
    public string FieldName { get; set; } = "";

    public List<string> Values { get; set; } = [];
}

/// <summary>One message in a document's chat thread, with the addresses its row advertised (ADR 0543).</summary>
public record ChatMessageResponse
{
    public Guid Id { get; set; }

    public Guid? ParentMessageId { get; set; }

    public string Body { get; set; } = "";

    public string AuthorName { get; set; } = "";

    public Guid? AuthorUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public int Kind { get; set; }

    public int? VersionNumber { get; set; }

    public string? VersionComment { get; set; }

    public int? VersionCommentKind { get; set; }

    public List<ChatMentionResponse>? Mentions { get; set; }

    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>A user mentioned in a chat message (issue #383).</summary>
public record ChatMentionResponse
{
    public Guid UserId { get; set; }

    public string DisplayName { get; set; } = "";
}

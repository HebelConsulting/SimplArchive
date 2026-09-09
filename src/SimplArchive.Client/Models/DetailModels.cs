using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>One index-data field and its values, as the detail pane displays them.</summary>
/// <remarks>
/// Shared: the Repositories detail pane and the Recycle bin's own isolated detail pane show the same thing
/// about a document, so they read one shape rather than two (ADR 0558).
/// </remarks>
public record FieldGroup
{
    public string FieldName { get; set; } = string.Empty;

    /// <summary>The field's declared type — what lets the read view render a DateTime value as a local
    /// wall clock instead of the raw ISO-with-offset wire string.</summary>
    public string DataType { get; set; } = "Text";

    public List<string> Values { get; set; } = [];

    /// <summary>The values as the pane SHOWS them — type-aware, matching the desktop's rendering (ADR 0511).</summary>
    public string Display => string.Join(", ", Values.Select(v =>
        DataType == "DateTime" ? SimplArchive.Presentation.IndexInstant.Display(v) : v));

    /// <summary>A Url field's values render as LINKS (ADR 0763) — the read row swaps the text for anchors.</summary>
    public bool IsUrl => DataType == "Url";

    /// <summary>A DocumentReference field's values name other documents (ADR 0773) — the row renders the
    /// resolved <see cref="Targets"/> rather than the raw ids in <see cref="Values"/>.</summary>
    public bool IsDocumentReference => DataType == "DocumentReference";

    /// <summary>The resolved targets, index-aligned with <see cref="Values"/>. Server-resolved, so the pane
    /// never fetches per value and never composes an address (ADRs 0543/0557).</summary>
    public List<FieldTarget> Targets { get; set; } = [];
}

/// <summary>One resolved target of a DocumentReference value (ADR 0773).</summary>
/// <remarks>
/// A target the caller may not open arrives with no name and no link, and is drawn as unavailable — the
/// server has already decided that, and the client must not try to improve on it by showing the id.
/// </remarks>
public record FieldTarget
{
    public Guid Id { get; set; }

    public string? Name { get; set; }

    public List<LinkResponse> Links { get; set; } = [];

    /// <summary>Whether this target can be opened — the `document` rel's presence, which is the server
    /// saying "available to you, here, now" (ADR 0543).</summary>
    public bool CanOpen => Links.Any(l => l.Rel == "document");
}

/// <summary>One message in a document's chat thread, with the addresses its row advertised (ADR 0543).</summary>
public record ChatMessageResponse
{
    public Guid Id { get; set; }

    public Guid? ParentMessageId { get; set; }

    public string Body { get; set; } = string.Empty;

    public string AuthorName { get; set; } = string.Empty;

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

    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>
/// The subject's retention position (ADR 0326): how long it must be kept, when it becomes disposable, and
/// whether a legal hold is currently freezing that clock.
/// </summary>
public record DetailRetentionDto
{
    public int RetentionYears { get; set; }
    public string DispositionDate { get; set; } = string.Empty;
    public bool SuspendedByHold { get; set; }
}

/// <summary>A colleague's identity card, opened from their name in a chat thread.</summary>
public record UserCardResponse
{
    public Guid UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool HasPhoto { get; set; }
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>
/// One candidate for an "@" mention. The names behind a body's "@[id]" tokens are resolved by the SERVER so a
/// thread renders without a lookup per token (issue #383).
/// </summary>
public record MentionableUserResponse
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
}

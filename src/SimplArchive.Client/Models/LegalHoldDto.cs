using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>A legal hold (a matter) and what it freezes.</summary>
/// <remarks>
/// Shared because a hold is created from two places — the Legal holds tab and the contents-pane row action
/// that puts one document under a new matter — and one shape read two ways is what drifts (ADR 0558).
/// </remarks>
public record LegalHoldDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string? Reason { get; set; }

    public DateTimeOffset PlacedAt { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    public bool IsActive { get; set; }

    public int ItemCount { get; set; }

    public List<LegalHoldItemDto> Items { get; set; } = new();

    /// <summary>
    /// The addresses this hold advertised — <c>self</c> always, plus <c>release</c> and <c>add-item</c> only
    /// while it is active. The absence of the last two IS the answer for a released hold: its items are history
    /// rather than something to edit, so the affordance is not offered instead of being refused after the click
    /// (ADR 0543).
    /// </summary>
    public List<LinkResponse> Links { get; set; } = [];
}

/// <summary>One document a hold covers, with the pairing's own advertised address (ADR 0543).</summary>
public record LegalHoldItemDto
{
    public Guid DocumentId { get; set; }

    public string DocumentName { get; set; } = "";

    /// <summary>The document's home folder (null for a repository root) — what Go to navigates by.</summary>
    public Guid? ParentId { get; set; }

    /// <summary>Carries <c>remove</c> while the hold is active. The doc comment promised this long before the
    /// property existed, and the tab composed the path instead (#416).</summary>
    public List<LinkResponse> Links { get; set; } = [];
}

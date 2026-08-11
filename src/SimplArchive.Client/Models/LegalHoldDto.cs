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
}

/// <summary>One document a hold covers. `RemoveHref` is the pairing's own advertised address (ADR 0543).</summary>
public record LegalHoldItemDto
{
    public Guid DocumentId { get; set; }

    public string DocumentName { get; set; } = "";
}

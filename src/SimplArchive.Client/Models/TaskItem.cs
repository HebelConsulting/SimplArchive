namespace SimplArchive.Client.Models;

/// <summary>
/// A workflow task assigned to the caller — the version awaiting their review, and when it fell due.
/// </summary>
/// <remarks>
/// Shared because two tabs read it: the Tasks intray lists them, and the My work dashboard counts and
/// summarises them. One shape read two ways is what drifts (ADR 0558).
/// </remarks>
public record TaskItem
{
    public Guid DocumentId { get; set; }

    public Guid? ParentId { get; set; }

    public Guid VersionId { get; set; }

    public string DocumentName { get; set; } = "";

    public int? VersionNumber { get; set; }

    public DateTimeOffset AssignedAt { get; set; }

    public DateTimeOffset? DueAt { get; set; }

    public bool IsOverdue { get; set; }
}

/// <summary>The task-inbox listing envelope.</summary>
public record TaskListResponse
{
    public List<TaskItem> Tasks { get; set; } = [];
}

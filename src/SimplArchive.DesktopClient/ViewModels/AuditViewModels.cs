namespace SimplArchive.DesktopClient.ViewModels;

// A row in the desktop Audit tab (ADR "Desktop audit viewer") — a single recorded audit event, pre-formatted
// for the table.
public sealed class AuditEventRowViewModel
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string ActorName { get; init; }
    public required string ActorType { get; init; }
    public required string Action { get; init; }
    public string? TargetType { get; init; }
    public string? TargetName { get; init; }
    public string? Details { get; init; }

    public string When => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
    public string Actor => $"{ActorName} ({ActorType})";
    public string Target => string.IsNullOrEmpty(TargetName) ? TargetType ?? "" : $"{TargetType}: {TargetName}";
}

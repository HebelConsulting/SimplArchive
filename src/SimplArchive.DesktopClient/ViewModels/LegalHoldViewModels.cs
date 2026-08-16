namespace SimplArchive.DesktopClient.ViewModels;

// A legal-hold matter row in the Legal Holds tab (ADR "Legal hold & retention enforcement").
// Hold is the row the server sent, carried whole so release / add-item / re-read follow the addresses it
// advertised rather than paths rebuilt from an id (ADR 0543/0555).
public sealed class LegalHoldRowViewModel(Guid id, string name, bool isActive, int itemCount, SimplArchive.DesktopClient.Services.SimplArchiveApiClient.LegalHoldInfo hold)
{
    public SimplArchive.DesktopClient.Services.SimplArchiveApiClient.LegalHoldInfo Hold { get; } = hold;

    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsActive { get; } = isActive;
    public int ItemCount { get; } = itemCount;

    public string IconValue => "mdi-gavel";
    public string DisplayName => IsActive ? Name : $"{Name} (released)";
}

// A document covered by the selected hold.
// Item carries the pairing's own `remove` address — the only thing that knows both ends of it (ADR 0543/0555).
public sealed class LegalHoldItemRowViewModel(Guid documentId, string documentName, SimplArchive.DesktopClient.Services.SimplArchiveApiClient.LegalHoldItemInfo item)
{
    public SimplArchive.DesktopClient.Services.SimplArchiveApiClient.LegalHoldItemInfo Item { get; } = item;

    public Guid DocumentId { get; } = documentId;
    public string DocumentName { get; } = documentName;

    /// <summary>Remove is offered only while the pairing advertised it — a released hold's items are history (ADR 0543/0554).</summary>
    public bool CanRemove => Item.RemoveHref is not null;
}

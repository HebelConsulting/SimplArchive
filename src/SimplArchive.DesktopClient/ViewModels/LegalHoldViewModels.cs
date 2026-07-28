namespace SimplArchive.DesktopClient.ViewModels;

// A legal-hold matter row in the Legal Holds tab (ADR "Legal hold & retention enforcement").
public sealed class LegalHoldRowViewModel(Guid id, string name, bool isActive, int itemCount)
{
    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public bool IsActive { get; } = isActive;
    public int ItemCount { get; } = itemCount;

    public string IconValue => "mdi-gavel";
    public string DisplayName => IsActive ? Name : $"{Name} (released)";
}

// A document covered by the selected hold.
public sealed class LegalHoldItemRowViewModel(Guid documentId, string documentName)
{
    public Guid DocumentId { get; } = documentId;
    public string DocumentName { get; } = documentName;
}

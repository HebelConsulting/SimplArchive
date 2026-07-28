namespace SimplArchive.DesktopClient.ViewModels;

// A row in the contents list — a repository/folder, a document, or a reference (shortcut) to one.
public sealed class NodeViewModel
{
    // For a reference this is the TARGET's id, so Open/Save-as/detail all act on the referenced item.
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool HasChildren { get; init; }

    public required bool HasVersions { get; init; }

    // Reference (shortcut) metadata — see ADR "Desktop drag-and-drop move and reference". A reference row
    // points at an item filed elsewhere; ReferenceId identifies the shortcut (for delete/"Go to"), and
    // RealParentId is the target's real home folder (null = a repository root).
    public bool IsReference { get; init; }

    public Guid ReferenceId { get; init; }

    public Guid? RealParentId { get; init; }

    // True when at least one reference (shortcut) targets this item — drives the "References …" context-menu
    // entry. See ADR "References-of-an-item list".
    public bool HasReferences { get; init; }

    // True when directly under an active legal hold (ADR "Legal hold & retention enforcement") — the row shows
    // a lock and a "Place legal hold" / "Remove from hold" is offered.
    public bool OnLegalHold { get; init; }

    // Check-out state (ADR "Document check-out / check-in"): CheckedOut drives a lock glyph, CheckedOutByMe
    // colours it (mine vs someone else's) and gates the check-out vs override actions.
    public bool CheckedOut { get; init; }

    public bool CheckedOutByMe { get; init; }

    // The lock holder's display name — a checked-out row is shown as "[name] {Name}" (ADR "Check-out
    // working-copy stash").
    public string CheckedOutByName { get; init; } = "";

    // Checked out by a DIFFERENT user — the red lock + the "Override check-out" action.
    public bool CheckedOutByOther => CheckedOut && !CheckedOutByMe;

    // The row label: a checked-out document is prefixed with the holder's display name.
    public string DisplayName => CheckedOut && !string.IsNullOrEmpty(CheckedOutByName) ? $"[{CheckedOutByName}] {Name}" : Name;

    // Virtual rows shown while browsing a .zip's contents (ADR "Zip file browsing"): an archive entry (a file
    // inside the zip, ArchiveEntryPath set) or the "back" row that exits the archive view. Neither is a real
    // Document (Id is Guid.Empty).
    public bool IsArchiveEntry { get; init; }

    public bool IsArchiveBack { get; init; }

    public string? ArchiveEntryPath { get; init; }

    // List-row columns (ADR "List-row columns and sorting"): the assigned mask's name, the latest confirmed
    // version's document date + byte size, and the tags — all shown as sortable columns.
    public string DocumentType { get; init; } = "";

    public DateOnly? DocumentDate { get; init; }

    public long? SizeBytes { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    // The data-classification sensitivity label (ADR "Configurable sensitivity labels + upload defaults") — the
    // per-tenant label name + colour from the server; empty name = None (no badge). Drives the inline row badge.
    public string SensitivityLabelName { get; init; } = "";

    public string? SensitivityLabelColor { get; init; }

    public bool HasSensitivity => !string.IsNullOrEmpty(SensitivityLabelName);

    public string SensitivityText => SensitivityLabelName;

    public string SensitivityBrush => string.IsNullOrEmpty(SensitivityLabelColor) ? "#9e9e9e" : SensitivityLabelColor;

    // Column display strings (blank for a folder / version-less doc where they don't apply).
    public string TypeText => IsFolder ? "Folder" : DocumentType;
    public string DocumentDateText => DocumentDate?.ToString("yyyy-MM-dd") ?? "";
    public string TagsText => string.Join(", ", Tags);
    public string SizeText => SizeBytes switch
    {
        null => "",
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        _ => $"{SizeBytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    // Count of confirmed versions (ADR "Compare-versions gating + default") — gates the "Compare versions"
    // action, which needs >= 2 to have anything to diff. From the listing; 0 for a reference row (its listing
    // doesn't carry it), so Compare is offered on a reference's real row rather than the shortcut.
    public int VersionCount { get; init; }

    // A folder is a Document with no versions (ADR 0175); a document has versions.
    public bool IsFolder => !HasVersions;

    // Material Design Icons glyph name for the row. References get a shortcut variant; archive rows their own.
    public string IconValue => (IsArchiveBack, IsArchiveEntry) switch
    {
        (true, _) => "mdi-arrow-up-left",
        (_, true) => "mdi-file-outline",
        _ => (IsReference, IsFolder) switch
        {
            (true, true) => "mdi-folder-arrow-right",
            (true, false) => "mdi-file-link",
            (false, true) => "mdi-folder",
            (false, false) => "mdi-file-document-outline",
        },
    };
}

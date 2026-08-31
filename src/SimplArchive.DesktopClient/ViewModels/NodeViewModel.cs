namespace SimplArchive.DesktopClient.ViewModels;

// A row in the contents list — a repository/folder, a document, or a reference (shortcut) to one.
public sealed class NodeViewModel
{
    // For a reference this is the TARGET's id, so Open/Save-as/detail all act on the referenced item.
    // The row's advertised sub-resource addresses, carried through from the listing (ADR 0543, issue #416) so
    // an action opened from this row follows a rel instead of composing a path from the document id.
    //
    // Null for the SYNTHETIC rows — the Administration branch, the personal-space groupings — which stand for no
    // server resource at all. Href() therefore throws for them, which is right: there is nothing to follow.
    public IReadOnlyDictionary<string, string>? Links { get; init; }

    /// <summary>The advertised href for <paramref name="rel"/>; throws rather than composing one.</summary>
    public string Href(string rel) =>
        Links is not null && Links.TryGetValue(rel, out var href)
            ? href
            : throw new InvalidOperationException(
                $"The '{rel}' rel was not advertised for '{Name}'. Follow a rel the resource offers, or fetch the "
                + "resource — do not compose the URL (ADR 0543).");

    /// <summary>The advertised href for <paramref name="rel"/>, or null when the row does not offer it.</summary>
    /// <remarks>
    /// For the places where a missing rel is an ANSWER rather than a mistake — a folder has no versions, so
    /// asking for them is not a bug to throw on (ADR 0543: absence means "not available here"). Href stays the
    /// default, because composing past a rel the server withheld is what that rule exists to stop.
    /// </remarks>
    public string? TryHref(string rel) =>
        Links is not null && Links.TryGetValue(rel, out var href) ? href : null;

    /// <summary>
    /// The DOCUMENT resource's own address. A repository row calls its document view <c>document</c> — its
    /// <c>self</c> is the repository view (ADR 0200) — while every other row's <c>self</c> IS the document.
    /// </summary>
    public string DocumentSelfHref =>
        Links is not null && Links.TryGetValue("document", out var doc) ? doc : Href("self");

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required bool HasChildren { get; init; }

    public required bool HasVersions { get; init; }

    // Reference (shortcut) metadata — see ADR "Desktop drag-and-drop move and reference". A reference row
    // points at an item filed elsewhere; ReferenceId identifies the shortcut (for delete/"Go to"), and
    // RealParentId is the target's real home folder (null = a repository root).
    public bool IsReference { get; init; }

    /// <summary>The mask's icon token as the listing sent it, or null to keep the generic glyph.</summary>
    public string? MaskIconToken { get; init; }

    public Guid ReferenceId { get; init; }

    public Guid? RealParentId { get; init; }

    // True when at least one reference (shortcut) targets this item — drives the "References …" context-menu
    // entry. See ADR "References-of-an-item list".
    public bool HasReferences { get; init; }

    // True when directly under an active legal hold (ADR "Legal hold & retention enforcement") — the row shows
    // a lock and a "Place legal hold" / "Remove from hold" is offered.
    // What the SERVER says this caller may do to this row (#858) — the Rename and Delete gates. False by
    // default, which is the safe direction: absence means "not available to you, here, now" (ADR 0543), and a
    // synthetic row (an archive entry, the archive back-link, demo data) has nothing to rename or delete.
    public bool CanDelete { get; init; }

    public bool CanEditIndexData { get; init; }

    public bool CanMove { get; init; }

    public bool CanManagePermissions { get; init; }

    /// <summary>May a plain child be created in this row? (#854.)</summary>
    public bool CanCreateChildren { get; init; }

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

    // The entry's own advertised download address (ADR 0543) — the client never rebuilds it from the path.
    public string? ArchiveEntryDownloadHref { get; init; }

    // A reference row's own advertised `delete` address (ADR 0543); null for anything that is not a reference.
    public string? ReferenceDeleteHref { get; init; }

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
    // The SERVER's type — for a typed folder that is the mask name ("Addressbook", "Calendar", "Notebook").
    // Flattening every folder to "Folder" predates typed folders and discarded an answer the server was
    // already giving (#824); only the genuine plain Folder is localised, a mask name renders as itself.
    public string TypeText => IsFolder && (DocumentType.Length == 0 || DocumentType == "Folder")
        ? SimplArchive.Localization.Strings.Get("FolderType")
        : DocumentType;
    public string DocumentDateText => DocumentDate?.ToString("yyyy-MM-dd") ?? "";
    public string TagsText => string.Join(", ", Tags);

    /// <summary>Who filed the current version, falling back to who created the document (#768).</summary>
    public string CreatedBy { get; init; } = string.Empty;
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

    // The latest confirmed version's CreatedAt (filing timestamp) — the "Created" folder contents-sort key (ADR
    // "Per-folder contents sort order"). Null for a folder / version-less doc.
    public DateTimeOffset? VersionCreatedAt { get; init; }

    // A folder is a Document with no versions (ADR 0175); a document has versions.
    public bool IsFolder => !HasVersions;

    // Material Design Icons glyph name for the row. References get a shortcut variant; archive rows their own.
    //
    // An EMPTY folder takes the outline variant, exactly as in the tree (ADR 0547): the same object must not look
    // like two different things depending on which pane you are seeing it in. The tree used to own this rule
    // alone, which is how a folder came to read as empty on the left and full in the middle.
    public string IconValue => IsEmptyFolder ? $"{BaseIconValue}-outline" : BaseIconValue;

    private string BaseIconValue => (IsArchiveBack, IsArchiveEntry) switch
    {
        (true, _) => "mdi-arrow-up-left",
        (_, true) => "mdi-file-outline",
        // A reference keeps its shortcut glyph rather than the mask's: that this row is not where the object
        // lives is the more important thing to say, and the target's own row shows the mask icon anyway.
        _ => (IsReference, IsFolder) switch
        {
            (true, true) => "mdi-folder-arrow-right",
            (true, false) => "mdi-file-link",
            (false, true) => Services.MaskIcon.For(MaskIconToken) ?? "mdi-folder",
            // The generic document glyph is ALREADY an outline one, so a mask token here must be the plain
            // form — appending "-outline" to "mdi-file-document-outline" would name nothing. Items never
            // qualify as empty folders, so the suffix is never appended to them anyway.
            (false, false) => Services.MaskIcon.For(MaskIconToken) ?? "mdi-file-document-outline",
        },
    };

    // A real folder with nothing inside. Archive rows are excluded — they are zip contents, not folders whose
    // emptiness anybody can act on.
    public bool IsEmptyFolder => IsFolder && !HasChildren && !IsArchiveBack && !IsArchiveEntry;

    // Which of App.axaml's glyph brushes paints this row, keyed the same way the tree keys it. A DOCUMENT keeps
    // the list's own accent — gold means "a place documents live", and a document is not one.
    public bool UsesFolderBrush => IsFolder && !IsEmptyFolder && !IsArchiveBack && !IsArchiveEntry;

    public bool UsesEmptyFolderBrush => IsEmptyFolder;
}

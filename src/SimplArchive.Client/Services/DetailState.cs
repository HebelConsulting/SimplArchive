using SimplArchive.Client.Models;

namespace SimplArchive.Client.Services;

/// <summary>
/// Everything the index-data pane is currently describing: the subject, its stored values, and — while an edit
/// is open — the unsaved form.
/// </summary>
/// <remarks>
/// Held outside the pane component for the reason <see cref="TreeState"/> is, only more sharply (ADRs
/// 0511/0558): the workbench renders one tab at a time, so the pane is DISPOSED whenever the user visits Tasks
/// or Search. Stored values kept in the component would merely be re-fetched, but the <c>Edit*</c> half is
/// UNSAVED USER INPUT — a half-filled index form, a renamed document, a changed mask. Losing that because
/// someone glanced at another tab is the clearest possible case of the state a user is annoyed to lose.
///
/// Three families, deliberately distinct rather than one mutable set:
/// <list type="bullet">
/// <item><c>Sys*</c> — the document's own facts as stored (name, dates, current version, OCR languages).</item>
/// <item>the unprefixed properties — what the DETAIL RESOURCE said about it (mask, tags, retention, rights).</item>
/// <item><c>Edit*</c> — the form's working copy, populated on entering edit and discarded on cancel.</item>
/// </list>
/// Keeping the working copy separate is what makes Cancel a no-op rather than a reload (ADR 0550).
/// </remarks>
public sealed class DetailState
{
    // ---- The subject ---------------------------------------------------------------------------------

    /// <summary>
    /// What the pane is describing (issue #408). A selected row wins; with none, it is the open folder — so the
    /// pane always describes SOMETHING you can see, and a folder gets the same pane as a document rather than a
    /// thinner one of its own.
    /// </summary>
    public BrowseNode? Node { get; set; }

    /// <summary>The rels the open document's resource advertised, followed rather than composed (ADR 0543).</summary>
    public IReadOnlyDictionary<string, string>? Links { get; set; }

    /// <summary>True while a load or save is in flight, which disables the pane's commit controls.</summary>
    public bool Busy { get; set; }

    // ---- The document's own facts, as stored ---------------------------------------------------------

    public bool SysHasVersion { get; set; }
    public Guid SysCurrentVersionId { get; set; }
    public int? SysCurrentVersion { get; set; }
    public string SysName { get; set; } = "";
    public string SysFileExtension { get; set; } = "";
    public DateTime? SysDocumentDate { get; set; }
    public string SysCreated { get; set; } = "";
    public string SysCreatedBy { get; set; } = "";

    /// <summary>Whether the current version is a TIFF, which is what offers the searchable-PDF conversion.</summary>
    public bool SysHasTiff { get; set; }

    /// <summary>The per-version OCR languages (ADR 0272).</summary>
    public List<string> SysOcrCodes { get; set; } = [];

    // ---- What the detail resource said ----------------------------------------------------------------

    public string? MaskName { get; set; }
    public Guid? MaskId { get; set; }
    public int? VersionNumber { get; set; }
    public int VersionCount { get; set; }
    public List<FieldGroup>? IndexData { get; set; }
    public List<string>? Tags { get; set; }
    public bool Subscribed { get; set; }
    public DetailRetentionDto? Retention { get; set; }
    public bool CanManagePermissions { get; set; }
    public bool BreaksInheritance { get; set; }
    public Guid? SensitivityId { get; set; }
    public string SensitivityName { get; set; } = "";
    public string? SensitivityColor { get; set; }
    public bool SensitivityWatermark { get; set; }

    /// <summary>The subject's own contents-sort order (folders only), read from its detail resource.</summary>
    public FolderContentsSortOrder SortOrder { get; set; } = FolderContentsSortOrder.Name;

    /// <summary>The external-links address when the caller may manage them; <c>null</c> hides the affordance.</summary>
    public string? ExternalLinksHref { get; set; }

    // ---- The open edit's working copy -----------------------------------------------------------------

    /// <summary>
    /// Whether the pane is in edit mode. Here rather than in the pane or the editor for the same reason the
    /// working copy is: a tab switch disposes the pane, and coming back to a form that had silently reverted to
    /// read mode would discard the input below without saying so.
    /// </summary>
    public bool IsEditing { get; set; }

    public string EditName { get; set; } = "";
    public DateTime? EditDocumentDate { get; set; }
    public Guid? EditMaskId { get; set; }
    public List<string> EditOcrCodes { get; set; } = [];
    public List<string> EditTags { get; set; } = [];
    public Guid? EditSensitivityId { get; set; }
    public FolderContentsSortOrder EditSortOrder { get; set; } = FolderContentsSortOrder.Name;

    /// <summary>The half-typed value in the tag add-box — unsaved input, so it survives a tab switch too.</summary>
    public string? EditNewTag { get; set; }

    /// <summary>
    /// The mask's fields as an editable form. Readonly-collection on purpose: entering an edit REPLACES the
    /// contents rather than the list, so a component holding a reference keeps seeing the live form.
    /// </summary>
    public List<EditField> EditFields { get; } = [];

    // ---- What the working copy started from ------------------------------------------------------------

    // Snapshotted when the edit opens, so a save sends only what actually changed and each successful field
    // advances its own original — a partial save leaves the rest still marked dirty, which is the point. They
    // live beside the working copy rather than in DetailEditor because they are half of the same answer: without
    // them a form restored after a tab switch would think every field had changed.

    public string OrigName { get; set; } = "";
    public DateTime? OrigDocumentDate { get; set; }
    public Guid? OrigMaskId { get; set; }
    public List<string> OrigOcrCodes { get; set; } = [];
    public List<string> OrigTags { get; set; } = [];
}

using SimplArchive.Client.Models;
using SimplArchive.Localization;

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

    /// <summary>The current version's workflow state (raw enum name), null when none was started.</summary>
    public string? SysWorkflowStatus { get; set; }

    /// <summary>
    /// The transitions the server says this caller may make on this version's workflow, by rel (#691).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pane renders one button per entry and knows nothing about the state machine: which transitions are
    /// legal here, and which of those this caller may make, are both the server's answers (ADR 0543). Adding a
    /// state to the workflow therefore needs no client change — the labels are looked up per rel, and an
    /// unknown rel is simply not drawn rather than drawn wrong.
    /// </para>
    /// <para>
    /// Null means NOT FETCHED, which is also how it starts and what it returns to between documents — never
    /// "no transitions". The distinction matters because the fetch is deliberately skipped when the status
    /// already says there is nothing to offer (see the loader), so an empty set and an absent one must not be
    /// allowed to look alike (ADR 0559: an address must not outlive its subject).
    /// </para>
    /// </remarks>
    public Dictionary<string, string>? WorkflowLinks { get; set; }

    /// <summary>The transition rels, in the order the pane draws them — never the dictionary's own order.</summary>
    /// <remarks>
    /// Fixed rather than emitted-order: a row of buttons that reorders itself between documents is one the user
    /// must read instead of aim at, which is the same reason CreatableChildren sorts its menu. `submit` leads
    /// because in the one state that offers it beside nothing else, it IS the next step.
    /// </remarks>
    public static readonly string[] WorkflowTransitions = ["submit", "approve", "reject", "reassign", "release"];

    /// <summary>The advertised transitions, in draw order. Empty when none was fetched or none is offered.</summary>
    public IEnumerable<string> OfferedTransitions =>
        WorkflowLinks is null ? [] : WorkflowTransitions.Where(WorkflowLinks.ContainsKey);

    /// <summary>The workflow state, localised for the pane's Status row.</summary>
    public string WorkflowStateName => SysWorkflowStatus switch
    {
        null or "" => Strings.Get("WfNotStarted"),
        "Draft" => Strings.Get("WfStateDraft"),
        "InReview" => Strings.Get("WfStateInReview"),
        "Approved" => Strings.Get("WfStateApproved"),
        "Rejected" => Strings.Get("WfStateRejected"),
        "Released" => Strings.Get("WfStateReleased"),
        var other => other,
    };

    /// <summary>
    /// The workflow affordance labels itself by the document's state (review decision: Start when none,
    /// Manage while active, View once Released) — derived from the detail's payload, no extra request
    /// (ADR 0557). A row context menu can't know a non-selected row's state, so IT stays a neutral
    /// "Workflow…". Ribbon flavour is the bare label; the pane's button gets the trailing ellipsis.
    /// </summary>
    public string WorkflowRibbonLabel => SysWorkflowStatus switch
    {
        null or "" => Strings.Get("RibbonStartWorkflow"),
        "Released" => Strings.Get("RibbonViewWorkflow"),
        _ => Strings.Get("RibbonManageWorkflow"),
    };

    /// <inheritdoc cref="WorkflowRibbonLabel"/>
    public string WorkflowButtonLabel => SysWorkflowStatus switch
    {
        null or "" => Strings.Get("CtxStartWorkflow"),
        "Released" => Strings.Get("CtxViewWorkflow"),
        _ => Strings.Get("CtxManageWorkflow"),
    };
    public Guid SysCurrentVersionId { get; set; }

    /// <summary>The current version's advertised `document-date` address, captured from the version row when
    /// the detail loads — so saving an edited date follows the rel rather than rebuilding the path (ADR 0543).</summary>
    public string? SysDocumentDateHref { get; set; }
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

    /// <summary>
    /// Where this document's own mask keeps its FIELD DEFINITIONS — the <c>definition</c> rel on the mask
    /// resource (#729, ADR 0688).
    /// </summary>
    /// <remarks>
    /// The editor used to find them by looking the mask id up in the catalogue, which carries only the masks a
    /// user may freely CHOOSE (#671) — so a typed folder's editor opened with no fields at all and a Mailbox's
    /// address list could not be set. Captured here because the mask resource is already read when the pane
    /// loads: following the rel therefore costs nothing (ADR 0557).
    /// </remarks>
    public string? MaskDefinitionHref { get; set; }

    /// <summary>Takes everything the mask resource said, so the shell does not spell out four assignments.</summary>
    public void ApplyMask(MaskResponse? mask)
    {
        MaskName = mask?.Name;
        MaskId = mask?.MaskId;
        // Qualified: this class has a Links PROPERTY of its own, which shadows the helper's name here.
        MaskDefinitionHref = Hypermedia.Links.Href(mask?.Links, "definition");
        VersionNumber = mask?.VersionNumber;
    }

    public int? VersionNumber { get; set; }
    public int VersionCount { get; set; }
    public List<FieldGroup>? IndexData { get; set; }
    public List<string>? Tags { get; set; }
    public bool Subscribed { get; set; }
    public DetailRetentionDto? Retention { get; set; }
    public bool CanManagePermissions { get; set; }

    public bool CanEditIndexData { get; set; }
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

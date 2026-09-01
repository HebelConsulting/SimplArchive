using System;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What a tab view-model is allowed to ask of the shell around it.
/// </summary>
/// <remarks>
/// <para>
/// A tab owns its own state; what it cannot own is the things there is exactly ONE of — the status line, the
/// saved window layout, which tab is in front, and the detail pane describing whatever document is selected.
/// This is the whole of that list, and a tab reaches the shell through here or not at all.
/// </para>
/// <para>
/// <b>Why an interface rather than a settable callback.</b> Every tab used to expose an
/// <c>Action&lt;string&gt;? StatusReporter</c> that the shell assigned after construction. That shape failed in
/// the safe-looking direction: a tab whose wiring line was forgotten did not break, it silently reported
/// nothing — the same trap as a capability flag defaulting to <c>false</c> (ADR 0723). Taken as a constructor
/// argument, the seam cannot be forgotten, because omitting it does not compile. All seven tabs take it
/// (ADR 0730); the past tense here is the point, not a description of anything still in the tree.
/// </para>
/// <para>
/// <b>A view-supplied capability is a different case, and deliberately stays a settable callback.</b>
/// <c>CopyToClipboard</c>, <c>RequestClose</c> and <c>AnnotationDialog</c> are nullable properties a VIEW
/// assigns, and converting them to constructor arguments is NOT the next tranche of this work. The shell
/// exists when a tab is constructed, so it can be required; a window's clipboard and its owned dialogs belong
/// to a view that does not exist yet, and demanding one at construction would invert who builds whom. The
/// failure mode differs too: a forgotten status reporter is silent, while a forgotten clipboard callback
/// disables a button the user then reports.
/// </para>
/// <para>
/// <b>What does NOT belong here.</b> A callback only one tab wants — Search's <c>OpenResultRequested</c>, its
/// save-name prompt, Contacts' <c>Toggled</c> — stays a property on that tab. The test is whether more than
/// one tab asks it, or whether it is the shell's own state; anything else makes this the fat interface that
/// accumulates. The API client is deliberately absent for a different reason: it arrives at LOGIN rather than
/// at construction, so it stays a <c>SetApi</c> call.
/// </para>
/// </remarks>
/// <summary>
/// Putting a message on the window's one status line.
/// </summary>
/// <remarks>
/// Split out of <see cref="IShellContext"/> because a preview surface is a COMPONENT, not a tab: it needs to
/// report and nothing else, and handing it <c>SaveLayout</c> and <c>ActivateIntray</c> would be an interface
/// wider than its caller, which is how the fat interface starts. Constructor-injected all the same, so the
/// silent-failure mode a settable <c>Action&lt;string&gt;?</c> carries is gone from components too.
/// </remarks>
public interface IStatusReporter
{
    /// <summary>Puts a message on the single status line at the bottom of the window.</summary>
    void Report(string status);
}

public interface IShellContext : IStatusReporter
{
    /// <summary>Persists the window's pane sizes, including the tab's own panes.</summary>
    void SaveLayout();

    /// <summary>
    /// Brings the Intray tab to the front. Generalises to <c>ActivateTab(WorkbenchTab)</c> when a second tab
    /// needs it — today the integer tab indices are written bare, with a comment saying which tab each is.
    /// </summary>
    void ActivateIntray();

    /// <summary>
    /// Tells the shell a document changed on the server, so the detail pane can catch up if it happens to be
    /// the one on screen. The tab does not know whether it is — that is the shell's question to answer.
    /// </summary>
    Task DocumentChangedOnServerAsync(Guid documentId);

    /// <summary>The one drop-filing helper, shared with the shell's own check-out stash. Null before login.</summary>
    DropFiling? DropFiling { get; }

    /// <summary>The OCR language catalog, loaded once per session. Null before login.</summary>
    OcrLanguageCatalog? OcrLanguages { get; }

    /// <summary>Who is signed in. Session state, so the shell answers it rather than each tab tracking it.</summary>
    Guid? CurrentUserId { get; }

    /// <summary>
    /// The set of checked-out documents changed. The window reloads the folder it is showing and re-raises its
    /// own check-out counts — both its state, neither the tab's. Exactly one tab calls this, and it is on the
    /// interface anyway because it is the SHELL's state; the alternative left Check-out holding the one
    /// nullable settable callback this whole seam exists to remove.
    /// </summary>
    Task CheckoutsChangedAsync();
}

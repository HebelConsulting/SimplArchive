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
/// <b>Why an interface rather than the settable callbacks the other tabs use.</b> The six existing tab
/// view-models each expose an <c>Action&lt;string&gt;? StatusReporter</c> that the shell assigns after
/// construction. That shape fails in the safe-looking direction: a tab whose wiring line is forgotten does not
/// break, it silently reports nothing — the same trap as a capability flag defaulting to <c>false</c>
/// (ADR 0723). Taken as a constructor argument, the seam cannot be forgotten, because omitting it does not
/// compile.
/// </para>
/// <para>
/// <b>What does NOT belong here.</b> A callback only one tab wants — Search's <c>OpenResultRequested</c>, its
/// save-name prompt, Contacts' <c>Toggled</c> — stays a property on that tab. The test is whether more than
/// one tab asks it, or whether it is the shell's own state; anything else makes this the fat interface that
/// accumulates. The API client is deliberately absent for a different reason: it arrives at LOGIN rather than
/// at construction, so it stays a <c>SetApi</c> call.
/// </para>
/// </remarks>
public interface IShellContext
{
    /// <summary>Puts a message on the single status line at the bottom of the window.</summary>
    void Report(string status);

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
}

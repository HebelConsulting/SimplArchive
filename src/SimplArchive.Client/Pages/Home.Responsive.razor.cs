using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SimplArchive.Client.Pages;

// Which panes the viewport can show, as wbLayout.js reports it: a phone or an upright tablet (one pane, with
// the tree as a drawer and a full-screen detail overlay), a tablet held sideways (two panes of the three), or
// a desktop. Plus whether this device can HOVER at all, which decides whether the annotation authoring tools
// are offered (#349).
//
// These are here rather than in CSS because a media query cannot answer them at the moment they are needed: the
// single-pane flag is read at CLICK time for tap-to-navigate, and the tablet-landscape top bar is conditionally
// RENDERED rather than hidden, so the shell has to know the tier rather than merely look right in it.
//
// A partial of Home rather than a component, by ADR 0733's test: the markup these govern is the whole
// workbench -- the tree pane, the contents list, the detail overlay -- none of which can come with them.
//
// They arrived under a heading reading "Annotations", which had been a tombstone since ADR 0558 moved
// annotation authoring out to AnnotationEditor: the banner stayed and 482 lines of unrelated members
// accumulated beneath it (#941). It is also written with THREE dashes where every other heading in the file
// uses four, so a sweep over the banners does not even see it.
public partial class Home
{
    // Single-pane drill-down (ADR "Responsive phone drill-down"), reported by wbLayout.js: a phone, OR a tablet
    // held upright (#684) — one pane is one pane, and the two share every rule. Needed at CLICK time for
    // tap-to-navigate, which a media query cannot answer; the tree slides out as a drawer; a selected document
    // opens a full-screen detail overlay with Preview/Details/Comments sub-tabs.
    private bool _isSinglePane;

    // A tablet held sideways: two panes of the three. The CSS decides WHICH two from the selection alone, so
    // this exists for one thing the CSS cannot do — the top bar is conditionally RENDERED rather than hidden
    // (its folder-name text would otherwise collide with the desktop tests' text locators), so the shell has to
    // know when to emit it.
    private bool _isTabletLandscape;
    private bool _viewportWatched;
    // A touch-only device (no hover, coarse pointer) — hides the annotation authoring tools (#349); captured once
    // from wbLayout.js (a device capability). False on a hybrid with a mouse, so those keep full authoring.
    private bool _isTouchOnly;
    private bool _treeDrawerOpen;
    private string _phoneDetailTab = "preview";

    /// <summary>The viewport tier, as wbLayout.js reads it: phone / tablet-portrait / tablet-landscape / desktop.</summary>
    [JSInvokable]
    public Task OnViewportModeChanged(string mode)
    {
        var singlePane = mode is "phone" or "tablet-portrait";
        var tabletLandscape = mode is "tablet-landscape";
        if (_isSinglePane == singlePane && _isTabletLandscape == tabletLandscape)
        {
            return Task.CompletedTask;
        }

        _isSinglePane = singlePane;
        _isTabletLandscape = tabletLandscape;

        // The drawer is a touch-tier concept. Leaving BOTH tiers — rotating a tablet is not leaving — has to
        // close it, or the desktop layout renders with a translated tree nothing can dismiss.
        if (!singlePane && !tabletLandscape)
        {
            _treeDrawerOpen = false;
        }

        StateHasChanged();
        return Task.CompletedTask;
    }

    /// <summary>What a wbLayout.js from before #684 calls. Kept so a stale cached module still works.</summary>
    /// <remarks>
    /// ADR 0500's lesson: the module is cached independently of the app, so a client can be running last
    /// week's JS against today's Blazor. Renaming the only entry point would have made that combination fail
    /// to deserialise instead of degrading — this way it keeps the phone behaviour it already had, and gains
    /// the tablet tiers when its cache turns over.
    /// </remarks>
    [JSInvokable]
    public Task OnViewportChanged(bool isPhone) => OnViewportModeChanged(isPhone ? "phone" : "desktop");

    private void ToggleTreeDrawer() => _treeDrawerOpen = !_treeDrawerOpen;

    private void SetPhoneTab(string tab) => _phoneDetailTab = tab;

    // The detail overlay's back control — deselect the document to return to the folder's contents list.
    private void PhoneBack()
    {
        _selectedItem = null;
        _selectedNode = null;
    }
}

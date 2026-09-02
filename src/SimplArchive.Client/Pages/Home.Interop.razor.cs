using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace SimplArchive.Client.Pages;

// Wiring the workbench's JavaScript to the DOM: importing dropUpload.js and wbLayout.js, attaching the drop
// root, attaching the resizable/collapsible panes, starting the viewport watch, and scrolling the tree to a
// folder that was opened from elsewhere.
//
// Two rules govern everything here and are the reason it is one subject rather than incidental plumbing.
//
// The DOM-dependent wiring must run in the ALWAYS-EXECUTED part of OnAfterRenderAsync, never in the
// firstRender block: the workbench markup sits inside <Authorized>, so on a page reload -- where auth resolves
// asynchronously -- the elements do not exist yet at firstRender, and wiring them there silently never
// happens. Each JS side returns false until its root is real and true once wired, so this retries per render
// and the flags reset on a tab switch, because leaving Repositories tears the DOM down.
//
// And every call into wbLayout.js is wrapped: the module is cached independently of the app, so a client can
// be running last week's JS against today's Blazor. A missing export must degrade to the CSS default rather
// than throw through the render loop into Blazor's error UI (ADR 0500, issue #267).
//
// A partial rather than a component: these handles are the shell's own, and DisposeAsync in Home.razor
// releases them along with everything else the page holds.
public partial class Home
{
    private ElementReference _dropRoot;
    private IJSObjectReference? _module;
    private DotNetObjectReference<Home>? _selfRef;
    private ElementReference _layoutRoot;
    private IJSObjectReference? _layoutModule;
    private bool _dropRootWired;
    private bool _panesWired;

    // Set when the OPEN folder changes; acted on after the NEXT render, because the marked element does not
    // exist until the class has been rendered (#692). A folder opened from elsewhere — "Go to" from a search
    // hit — can land below the fold, where a correct mark is indistinguishable from no mark at all.
    private bool _scrollTreeCurrent;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_scrollTreeCurrent)
        {
            _scrollTreeCurrent = false;
            if (_layoutModule is not null)
            {
                // Wrapped like every other call into this module: a stale cached copy missing the export must
                // degrade to "the tree did not scroll" rather than throwing through the render loop (ADR 0500).
                try { await _layoutModule.InvokeAsync<bool>("scrollTreeCurrentIntoView"); }
                catch (JSException) { }
            }
        }

        if (firstRender)
        {
            // Import the JS modules up front (no DOM dependency). The DOM-dependent wiring below must NOT
            // live here: the workbench markup is inside <Authorized>, so on a page reload (auth resolving
            // asynchronously) the elements don't exist yet at firstRender — wiring them here would throw and
            // abort this block before the second import even ran.
            _selfRef = DotNetObjectReference.Create(this);
            _module = await JS.InvokeAsync<IJSObjectReference>("import", "./dropUpload.js");
            try
            {
                _layoutModule = await JS.InvokeAsync<IJSObjectReference>("import", "./wbLayout.js");
            }
            catch (JSException ex)
            {
                // A missing wbLayout.js must not crash the workbench — the panes just stay at their CSS defaults
                // (issue #267). Leaving _layoutModule null makes the interop below no-op.
                Console.Error.WriteLine($"wbLayout.js failed to load: {ex.Message}");
            }
        }

        // Wire the drop root and the resizable/collapsible panes on whichever render first has the real
        // elements. Both JS sides return false (no-op) until their root is a real element and true once
        // wired, so we simply retry until each succeeds; the flags reset on a tab switch (SetTab) so we
        // re-wire the freshly-recreated DOM when returning to the Repositories tab.
        if (_activeTab == Tab.Repositories)
        {
            if (!_dropRootWired && _module is not null)
            {
                _dropRootWired = await _module.InvokeAsync<bool>("initDropRoot", _dropRoot, _selfRef);
            }
            // Both wbLayout.js calls are wrapped so a stale cached copy missing an export (e.g. watchViewport,
            // added in ADR "responsive web workbench") degrades quietly to the CSS-default layout instead of
            // triggering the Blazor error UI, and stops retrying so it can't spam every render (issue #267).
            if (!_panesWired && _layoutModule is not null)
            {
                try
                {
                    _panesWired = await _layoutModule.InvokeAsync<bool>("attach", _layoutRoot);
                }
                catch (JSException ex)
                {
                    _panesWired = true; // give up rather than retry-spam a broken/stale module
                    Console.Error.WriteLine($"wbLayout.js attach failed (stale cache?): {ex.Message}");
                }
            }
            if (!_viewportWatched && _layoutModule is not null)
            {
                try
                {
                    await _layoutModule.InvokeVoidAsync("watchViewport", _selfRef);
                    // Capture the touch-only-device flag once (gates the annotation authoring tools, #349). Guarded
                    // in the same try so a stale cached module missing the export degrades quietly to false.
                    _isTouchOnly = await _layoutModule.InvokeAsync<bool>("isTouchOnly");
                }
                catch (JSException ex)
                {
                    Console.Error.WriteLine($"wbLayout.js watchViewport failed (stale cache?): {ex.Message}");
                }
                finally
                {
                    _viewportWatched = true;
                }
            }
        }

        // Replay a preview requested before its pane existed (see ShowPreviewAsync). In the always-executed
        // part, because a pane appears on whichever render first carries its markup — not necessarily the
        // page's first render — and LAST, after the drop/pane wiring above: this is the only await in here
        // that can take real time (a JS preview load), and the wiring must not queue behind it.
        if (_pendingPreview is { } pending)
        {
            if (_repoPreview is not null)
            {
                _pendingPreview = null;
                await ShowPreviewAsync(pending.Url, pending.Layout, pending.Converted, pending.HasVersion);
                StateHasChanged();
            }
        }
    }
}

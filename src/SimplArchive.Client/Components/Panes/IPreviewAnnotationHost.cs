namespace SimplArchive.Client.Components.Panes;

/// <summary>
/// What a preview pane reports back when the user draws on it — implemented by whoever owns the annotations.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PreviewPane"/> owns the JS host and therefore the <c>DotNetObjectReference</c> that preview.js
/// calls into, so every annotation gesture arrives at the pane first. But the pane cannot ACT on one: placing a
/// note means POSTing to the current version's annotations collection and reloading the list, which is document
/// state the pane deliberately does not hold. So the pane receives the gesture and forwards it here.
/// </para>
/// <para>
/// An interface rather than ten <c>EventCallback</c> parameters: the implementing methods already exist with
/// exactly these signatures, so the interface costs a declaration where ten parameters would cost ten
/// declarations, ten forwarders and ten call-site bindings for one cohesive contract that is always wired
/// together or not at all.
/// </para>
/// <para>
/// A pane with no annotations — the inbox, the recycle bin — passes <c>null</c> and every gesture is a no-op,
/// which is the correct behaviour rather than a missing feature: there is nothing to annotate on a staged file
/// or a deleted document.
/// </para>
/// </remarks>
public interface IPreviewAnnotationHost
{
    /// <summary>A drag with a markup tool active finished — create the highlight/rectangle/arrow.</summary>
    Task OnShapeDrawnAsync(int pageIndex, int kind, double x, double y, double width, double height);

    /// <summary>A click while "add note" is armed — create a sticky note at this spot.</summary>
    Task OnAnnotationPlacedAsync(int pageIndex, double x, double y);

    /// <summary>An existing marker was clicked — open it for viewing/editing.</summary>
    Task OnAnnotationClickedAsync(Guid id);

    /// <summary>A marker was dragged to a new position on its page.</summary>
    Task OnAnnotationMovedAsync(Guid id, int pageIndex, double x, double y);

    /// <summary>A marker was resized.</summary>
    Task OnAnnotationResizedAsync(Guid id, int pageIndex, double width, double height);

    /// <summary>A marker was selected; <paramref name="toggle"/> is a Ctrl/Cmd-click adding to the selection.</summary>
    Task OnAnnotationSelectAsync(Guid id, bool toggle);

    /// <summary>A marquee drag selected several markers at once.</summary>
    Task OnAnnotationMarqueeAsync(Guid[] ids, bool additive);

    /// <summary>A click on empty page area cleared the selection.</summary>
    Task OnAnnotationClearSelectionAsync();

    /// <summary>The whole selection was dragged by an offset.</summary>
    Task OnAnnotationGroupMoveAsync(int pageIndex, double dx, double dy);
}

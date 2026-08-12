namespace SimplArchive.Client.Components.Panes;

/// <summary>
/// What an annotation editor needs from the preview it draws on — implemented by <see cref="PreviewPane"/>.
/// </summary>
/// <remarks>
/// The mirror of <see cref="IPreviewAnnotationHost"/>. That one carries gestures OUT of the pane, because the
/// pane owns the JS host and therefore the callbacks but cannot act on a document's annotations; this one
/// carries the resulting marker state back IN, because only the pane can reach preview.js. Two interfaces
/// rather than a mutual concrete reference: the editor is a service and the pane is a component, and a service
/// that names a component is a dependency pointing the wrong way.
/// </remarks>
public interface IAnnotationSurface
{
    /// <summary>Whether a preview has actually rendered pages — there is nothing to draw markers on otherwise.</summary>
    bool HasPages { get; }

    /// <summary>Replaces the drawn markers wholesale with this set (empty hides them).</summary>
    Task SetAnnotationsAsync(object markers);

    /// <summary>Arms or disarms "click the page to place a note".</summary>
    Task SetAddModeAsync(bool on);

    /// <summary>Sets the active drawing tool: 0 none, 1 highlight, 2 rectangle, 3 arrow.</summary>
    Task SetDrawModeAsync(int kind);

    /// <summary>
    /// Outlines exactly these markers as selected. Toggles the outline on the EXISTING elements rather than
    /// rebuilding them, which is what keeps double-click-to-edit working.
    /// </summary>
    Task SetSelectionAsync(string[] ids);
}

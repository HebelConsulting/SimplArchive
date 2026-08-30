using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using SimplArchive.Client.Components.Panes;
using SimplArchive.Client.Dialogs;
using SimplArchive.Localization;

namespace SimplArchive.Client.Services;

/// <summary>
/// Sticky notes and markup shapes on the previewed document version (ADRs "Document annotations", "Annotation
/// markup", "Annotation multi-select", "Highlighting redesign") — the authoring mode, the selection, the
/// clipboard, and every write that follows a gesture on the page.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from the workbench page (ADR 0558). It implements <see cref="IPreviewAnnotationHost"/>, which the
/// page already implemented on the pane's behalf: <see cref="PreviewPane"/> owns the JS host and therefore the
/// <c>DotNetObjectReference</c> preview.js calls into, but it cannot act on a gesture, because acting means
/// writing to a version's annotation collection. The gesture arrives at the pane and is forwarded here.
/// </para>
/// <para>
/// A service rather than a component, for rule 4's reason: the Repositories tab body is behind
/// <c>@if (_activeTab == Tab.Repositories)</c>, so the pane and anything inside it is DISPOSED whenever the user
/// glances at Tasks or Search. The loaded markers are fetched data and would rightly reload — but the
/// clipboard, the armed tool and the chosen colour are work the user did, and losing a copied selection to a
/// tab switch is exactly the state ADR 0558 says must live outside the component.
/// </para>
/// <para>
/// Every gesture originates in JavaScript, so nothing re-renders on its own — a JS-invoked path does not trigger
/// a Blazor render the way a UI event handler does. <see cref="Changed"/> is how the toolbar learns that its
/// enabled/active states moved; forgetting to raise it looks exactly like a dead button.
/// </para>
/// </remarks>
public sealed class AnnotationEditor(HttpClient http, IDialogService dialogs, ISnackbar snackbar) : IPreviewAnnotationHost
{
    /// <summary>The draw colour palette (ADR "Highlighting redesign").</summary>
    public static readonly string[] Palette = ["#FFEB3B", "#8BC34A", "#4FC3F7", "#FF8A80", "#FFB74D", "#CE93D8"];

    private const string DefaultColor = "#FFEB3B";

    private readonly HashSet<Guid> _selectedIds = [];
    private readonly List<AnnotationDto> _clipboard = [];
    private List<AnnotationDto> _annotations = [];
    private IAnnotationSurface? _surface;
    private string? _collectionHref;

    /// <summary>Raised whenever something the toolbar renders has changed. See the remarks on the class.</summary>
    public event Action? Changed;

    /// <summary>Whether this version can carry notes at all — a real version, rendered, with an annotations rel.</summary>
    public bool Annotatable { get; private set; }

    /// <summary>Whether the caller holds CanAnnotate on the document (ADR "CanAnnotate right").</summary>
    public bool CanCreate { get; private set; }

    public bool Visible { get; private set; } = true;

    /// <summary>Whether a click on the page will place a note.</summary>
    public bool AddMode { get; private set; }

    /// <summary>The armed markup tool: 0 none, 1 highlight, 2 rectangle, 3 arrow.</summary>
    public int Tool { get; private set; }

    /// <summary>The colour new shapes are drawn in, and what a swatch click recolours the selection to.</summary>
    public string Color { get; private set; } = DefaultColor;

    public bool HasSelection { get; private set; }

    public bool HasClipboard { get; private set; }

    /// <summary>Points the editor at the preview it draws on; <c>null</c> while no pane is mounted.</summary>
    public void Attach(IAnnotationSurface? surface) => _surface = surface;

    /// <summary>The version's <c>annotations</c> collection, taken from the version's own rel (ADR 0543).</summary>
    public void UseCollection(string? href) => _collectionHref = href;

    /// <summary>
    /// Notes are available once a real version's preview has actually rendered pages. Loads them when so.
    /// </summary>
    public async Task ApplyAnnotatableAsync(bool hasVersion)
    {
        Annotatable = hasVersion && (_surface?.HasPages ?? false) && _collectionHref is not null;
        if (Annotatable)
        {
            await LoadAsync();
        }

        Notify(); // this is what makes the toolbar APPEAR — without it the tools never render at all
    }

    /// <summary>
    /// Drops everything belonging to the previewed subject — the markers, the selection, the clipboard and the
    /// authoring mode — for when the pane stops describing this document.
    /// </summary>
    public void Clear()
    {
        Annotatable = false;
        AddMode = false;
        Tool = 0;
        _collectionHref = null;
        _annotations = [];
        _selectedIds.Clear();
        _clipboard.Clear();
        HasSelection = false;
        HasClipboard = false;
        Notify();
    }

    // ---- Toolbar commands ------------------------------------------------------------------------------

    public async Task SetColorAsync(string color)
    {
        Color = color;
        // A swatch both sets the draw colour and recolours what is selected, so picking one is never a mode
        // change the user then has to apply.
        if (_collectionHref is not null && _selectedIds.Count > 0)
        {
            foreach (var a in Editable(_selectedIds))
            {
                if (!await PutAsync(a, a.PositionX, a.PositionY, a.Width, a.Height, a.Text, color, "StErrRecolour"))
                {
                    break;
                }
            }

            await LoadAsync();
        }

        Notify();
    }

    public async Task ToggleVisibilityAsync()
    {
        Visible = !Visible;
        if (!Visible && AddMode)
        {
            await StopAddNoteAsync();
        }

        await PushAsync();
        Notify();
    }

    public async Task StartAddNoteAsync()
    {
        await EnsureVisibleAsync();
        await SetToolAsync(0); // notes and shape-drawing are mutually exclusive
        AddMode = true;
        if (_surface is not null)
        {
            await _surface.SetAddModeAsync(true);
        }

        snackbar.Add(Strings.Get("StAnnClickToPlace"), Severity.Info);
        Notify();
    }

    public async Task StopAddNoteAsync()
    {
        AddMode = false;
        if (_surface is not null)
        {
            await _surface.SetAddModeAsync(false);
        }
    }

    /// <summary>Arms a markup tool; clicking the armed one disarms it. Drag on the page to draw.</summary>
    public async Task SelectToolAsync(int kind)
    {
        await EnsureVisibleAsync();
        await StopAddNoteAsync();
        await SetToolAsync(Tool == kind ? 0 : kind);
        if (Tool > 0)
        {
            snackbar.Add(Strings.Get("StAnnDragToDraw"), Severity.Info);
        }

        Notify();
    }

    public void CopySelection()
    {
        _clipboard.Clear();
        _clipboard.AddRange(_annotations.Where(a => _selectedIds.Contains(a.Id)));
        HasClipboard = _clipboard.Count > 0;
        if (HasClipboard)
        {
            snackbar.Add(string.Format(Strings.Get("StCopiedAnnotations"), _clipboard.Count), Severity.Info);
        }

        Notify();
    }

    /// <summary>Pastes the clipboard as offset duplicates on their original pages.</summary>
    public async Task PasteAsync()
    {
        if (_collectionHref is null || _clipboard.Count == 0 || !CanCreate)
        {
            return;
        }

        const double offset = 0.03;
        foreach (var a in _clipboard)
        {
            var ok = await PostAsync(
                a.PageIndex, a.Kind,
                Math.Clamp(a.PositionX + offset, 0, 1), Math.Clamp(a.PositionY + offset, 0, 1),
                a.Width ?? (a.Kind == 0 ? 0.22 : 0.1), a.Height ?? (a.Kind == 0 ? 0.06 : 0.05),
                a.Text, a.Color, "StErrPasteAnnotations");
            if (!ok)
            {
                break;
            }
        }

        await LoadAsync();
        Notify();
    }

    /// <summary>Deletes every selected annotation the caller may delete.</summary>
    public async Task DeleteSelectionAsync()
    {
        if (_collectionHref is null || _selectedIds.Count == 0)
        {
            return;
        }

        foreach (var a in _annotations.Where(a => a.CanDelete && _selectedIds.Contains(a.Id)).ToList())
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, HrefFor(a));
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{a.Etag}\"");
            if (!(await http.SendAsync(request)).IsSuccessStatusCode)
            {
                snackbar.Add(Strings.Get("StErrDeleteSelection"), Severity.Error);
                break;
            }
        }

        _selectedIds.Clear();
        await LoadAsync();
        Notify();
    }

    // ---- Gestures forwarded from the preview (IPreviewAnnotationHost) ----------------------------------

    public async Task OnShapeDrawnAsync(int pageIndex, int kind, double x, double y, double width, double height)
    {
        if (_collectionHref is null || kind <= 0)
        {
            return;
        }

        await SetToolAsync(0); // one shape per tool activation (ADR "Draw-tool behaviour")
        if (await PostAsync(pageIndex, kind, x, y, width, height, "", Color, "StErrAddMarkup"))
        {
            await LoadAsync();
        }

        Notify();
    }

    public async Task OnAnnotationPlacedAsync(int pageIndex, double x, double y)
    {
        await StopAddNoteAsync(); // one note per Add-note activation (ADR "Draw-tool behaviour")
        if (_collectionHref is null)
        {
            return;
        }

        var dialog = await dialogs.ShowAsync<AnnotationDialog>(Strings.Get("AnnNewNoteTitle"), new DialogParameters
        {
            ["Text"] = "",
            ["NoteColor"] = DefaultColor,
            ["CanEdit"] = true,
            ["CanDelete"] = false,
        });
        if ((await dialog.Result) is not { Canceled: false, Data: AnnotationDialog.AnnotationEditResult edit } || edit.Action != "save")
        {
            return;
        }

        // Created with a default size so it renders as an always-visible box showing its text (ADR "Post-it note
        // boxes" web parity), which the author can then resize.
        if (await PostAsync(pageIndex, 0, x, y, 0.22, 0.06, edit.Text, edit.Color, "StErrAddNote"))
        {
            await LoadAsync();
        }

        Notify();
    }

    public async Task OnAnnotationClickedAsync(Guid id)
    {
        if (Find(id) is not { } note || _collectionHref is null)
        {
            return;
        }

        var isShape = note.Kind > 0;
        var dialog = await dialogs.ShowAsync<AnnotationDialog>(isShape ? "Markup" : "Note", new DialogParameters
        {
            ["Text"] = note.Text,
            ["NoteColor"] = note.Color,
            ["AuthorName"] = note.AuthorName,
            ["CanEdit"] = note.CanEdit,
            ["CanDelete"] = note.CanDelete,
            ["IsShape"] = isShape,
        });
        if ((await dialog.Result) is not { Canceled: false, Data: AnnotationDialog.AnnotationEditResult edit })
        {
            return;
        }

        bool ok;
        if (edit.Action == "delete")
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, HrefFor(note));
            request.Headers.TryAddWithoutValidation("If-Match", $"\"{note.Etag}\"");
            ok = (await http.SendAsync(request)).IsSuccessStatusCode;
            if (!ok)
            {
                snackbar.Add(Strings.Get("StErrDeleteNote"), Severity.Error);
            }
        }
        else
        {
            ok = await PutAsync(note, note.PositionX, note.PositionY, note.Width, note.Height, edit.Text, edit.Color, "StErrSaveNote");
        }

        if (ok)
        {
            await LoadAsync();
        }

        Notify();
    }

    /// <summary>
    /// A note was dragged to a new spot. Persisted with the same PUT, keeping text/colour and using the etag the
    /// list already embeds; on failure a reload snaps it back to where it really is.
    /// </summary>
    public Task OnAnnotationMovedAsync(Guid id, int pageIndex, double x, double y) =>
        NudgeAsync(id, a => (x, y, a.Width, a.Height), "StErrMoveNote");

    /// <summary>A note box's corner grip was dragged (ADR "Post-it note boxes" web parity).</summary>
    public Task OnAnnotationResizedAsync(Guid id, int pageIndex, double width, double height) =>
        NudgeAsync(id, a => (a.PositionX, a.PositionY, width, height), "StErrResizeNote");

    // Move and resize are the same operation with a different field changing, so they are one implementation:
    // persist, then update IN PLACE from the response etag rather than reloading. A reload transiently empties
    // the list, which makes an immediate follow-up drag land on nothing and flickers besides.
    private async Task NudgeAsync(Guid id, Func<AnnotationDto, (double X, double Y, double? W, double? H)> change, string errorKey)
    {
        if (Find(id) is not { CanEdit: true } note || _collectionHref is null)
        {
            return;
        }

        var (x, y, w, h) = change(note);
        using var request = new HttpRequestMessage(HttpMethod.Put, HrefFor(note))
        {
            Content = JsonContent.Create(new { pageIndex = note.PageIndex, positionX = x, positionY = y, width = w, height = h, text = note.Text, color = note.Color }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{note.Etag}\"");
        var resp = await http.SendAsync(request);
        if (resp.IsSuccessStatusCode)
        {
            (note.PositionX, note.PositionY, note.Width, note.Height) = (x, y, w, h);
            if (resp.Headers.ETag?.Tag is { } tag)
            {
                note.Etag = tag.Trim('"');
            }

            await PushAsync();
        }
        else
        {
            snackbar.Add(Strings.Get(errorKey), Severity.Error);
            await LoadAsync(); // snap back to the persisted position + refresh the etag
        }

        Notify();
    }

    /// <summary>A plain click selects just this one; a Ctrl/Cmd-click toggles it in the set.</summary>
    public async Task OnAnnotationSelectAsync(Guid id, bool toggle)
    {
        if (!toggle)
        {
            _selectedIds.Clear();
            _selectedIds.Add(id);
        }
        else if (!_selectedIds.Remove(id))
        {
            _selectedIds.Add(id);
        }

        await UpdateSelectionAsync();
    }

    /// <summary>A marquee drag selected everything it enclosed (Ctrl adds to the current selection).</summary>
    public async Task OnAnnotationMarqueeAsync(Guid[] ids, bool additive)
    {
        if (!additive)
        {
            _selectedIds.Clear();
        }

        foreach (var id in ids)
        {
            _selectedIds.Add(id);
        }

        await UpdateSelectionAsync();
    }

    public async Task OnAnnotationClearSelectionAsync()
    {
        if (_selectedIds.Count == 0)
        {
            return;
        }

        _selectedIds.Clear();
        await UpdateSelectionAsync();
    }

    /// <summary>The whole selection was dragged — shift every selected editable annotation on that page.</summary>
    public async Task OnAnnotationGroupMoveAsync(int pageIndex, double dx, double dy)
    {
        if (_collectionHref is null)
        {
            return;
        }

        var targets = Editable(_selectedIds).Where(a => a.PageIndex == pageIndex).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        foreach (var a in targets)
        {
            var ok = await PutAsync(a,
                Math.Clamp(a.PositionX + dx, 0, 1), Math.Clamp(a.PositionY + dy, 0, 1),
                a.Width, a.Height, a.Text, a.Color, "StErrMoveSelection");
            if (!ok)
            {
                break;
            }
        }

        await LoadAsync();
        Notify();
    }

    // ---- Loading + drawing -----------------------------------------------------------------------------

    private async Task LoadAsync()
    {
        _annotations = [];
        CanCreate = false;
        if (_collectionHref is not null)
        {
            try
            {
                var list = await http.GetFromJsonAsync<AnnotationListDto>(_collectionHref.TrimStart('/'));
                _annotations = list?.Annotations ?? [];
                CanCreate = list?.CanCreate ?? false;
            }
            catch (Exception)
            {
                // No notes, or not readable — an empty overlay, not an error the user can act on.
            }
        }

        // Drop selection ids that no longer resolve to a loaded annotation (deleted elsewhere).
        _selectedIds.RemoveWhere(id => _annotations.All(a => a.Id != id));
        HasSelection = _selectedIds.Count > 0;
        await PushAsync();
    }

    private async Task PushAsync()
    {
        if (_surface is not { HasPages: true })
        {
            return;
        }

        var markers = Visible
            ? _annotations.Select(a => new { id = a.Id, pageIndex = a.PageIndex, kind = a.Kind, x = a.PositionX, y = a.PositionY, w = a.Width, h = a.Height, text = a.Text, color = a.Color, canEdit = a.CanEdit, points = a.Points, selected = _selectedIds.Contains(a.Id) }).ToArray()
            : [];
        // Passed as ONE object: InvokeVoidAsync's last parameter is `params object?[]`, so handing it the array
        // directly would spread the elements as separate JS arguments instead of passing the whole array.
        await _surface.SetAnnotationsAsync(markers);
    }

    private async Task UpdateSelectionAsync()
    {
        HasSelection = _selectedIds.Count > 0;
        if (_surface is not null)
        {
            await _surface.SetSelectionAsync(_selectedIds.Select(g => g.ToString()).ToArray());
        }

        Notify();
    }

    private async Task EnsureVisibleAsync()
    {
        if (!Visible)
        {
            Visible = true;
            await PushAsync();
        }
    }

    private async Task SetToolAsync(int kind)
    {
        Tool = kind;
        if (_surface is not null)
        {
            await _surface.SetDrawModeAsync(kind);
        }
    }

    // ---- Requests --------------------------------------------------------------------------------------

    /// <summary>This annotation's OWN address, as its row advertised it (#862).</summary>
    /// <remarks>
    /// Was `$"{_collectionHref}/{a.Id}"` — a path-segment append onto a rel-supplied href, which ADR 0557 calls
    /// composing in disguise. The hypermedia ratchet could not see it: its regex matches only literals that
    /// START with `api/`, and this one starts with an interpolation hole. The server had advertised the `self`
    /// rel on every annotation all along.
    /// </remarks>
    private static string HrefFor(AnnotationDto a) =>
        a.Links?.FirstOrDefault(l => l.Rel == "self")?.Href?.TrimStart('/')
        ?? throw new InvalidOperationException("The annotation advertised no 'self' rel (ADR 0543).");

    private AnnotationDto? Find(Guid id) => _annotations.FirstOrDefault(a => a.Id == id);

    private List<AnnotationDto> Editable(HashSet<Guid> ids) =>
        _annotations.Where(a => a.CanEdit && ids.Contains(a.Id)).ToList();

    private async Task<bool> PutAsync(AnnotationDto a, double x, double y, double? w, double? h, string text, string color, string errorKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, HrefFor(a))
        {
            Content = JsonContent.Create(new { pageIndex = a.PageIndex, positionX = x, positionY = y, width = w, height = h, text, color }),
        };
        request.Headers.TryAddWithoutValidation("If-Match", $"\"{a.Etag}\"");
        var ok = (await http.SendAsync(request)).IsSuccessStatusCode;
        if (!ok)
        {
            snackbar.Add(Strings.Get(errorKey), Severity.Error);
        }

        return ok;
    }

    private async Task<bool> PostAsync(int pageIndex, int kind, double x, double y, double? w, double? h, string text, string color, string errorKey)
    {
        var resp = await http.PostAsJsonAsync(_collectionHref!.TrimStart('/'),
            new { pageIndex, kind, positionX = x, positionY = y, width = w, height = h, text, color });
        if (!resp.IsSuccessStatusCode)
        {
            snackbar.Add(Strings.Get(errorKey), Severity.Error);
        }

        return resp.IsSuccessStatusCode;
    }

    private void Notify() => Changed?.Invoke();

    private sealed class AnnotationListDto
    {
        public List<AnnotationDto> Annotations { get; set; } = [];

        public bool CanCreate { get; set; }
    }

    private sealed class AnnotationDto
    {
        public Guid Id { get; set; }

        public int PageIndex { get; set; }

        public int Kind { get; set; }

        public double PositionX { get; set; }

        public double PositionY { get; set; }

        public double? Width { get; set; }

        public double? Height { get; set; }

        public string Text { get; set; } = "";

        public string Color { get; set; } = "";

        public string AuthorName { get; set; } = "";

        public string Etag { get; set; } = "";

        public bool CanEdit { get; set; }

        public bool CanDelete { get; set; }

        // The row's own address — what HrefFor follows instead of composing one (#862).
        public List<Hypermedia.LinkResponse>? Links { get; set; }

        public string? Points { get; set; }
    }
}

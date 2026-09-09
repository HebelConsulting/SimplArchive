using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;

namespace SimplArchive.DesktopClient.ViewModels;

// A row in the index-data pane: a field name and its (joined) value(s).
public sealed partial class IndexFieldViewModel
{
    public required string FieldName { get; init; }

    public required string Values { get; init; }

    /// <summary>A Url-typed field renders its values as LINKS that open the OS browser (ADR 0763) — the
    /// affordance the flight-planning DABS "Official source" field was waiting for. Everything else keeps
    /// the plain text row.</summary>
    public bool IsUrl { get; init; }

    /// <summary>The individual values, for the link template (the joined <see cref="Values"/> string cannot
    /// be clicked apart).</summary>
    public IReadOnlyList<string> UrlValues { get; init; } = [];

    /// <summary>A DocumentReference-typed field names other documents (ADR 0773) and renders them as
    /// openable rows — the workbench reveals the target, rather than the OS opening anything.</summary>
    public bool IsDocumentReference { get; init; }

    /// <summary>Whether the plain joined-text row is the right rendering — false for every type that draws
    /// its own. Stated once here rather than as a negation per type in the template, because a template that
    /// hides on <c>!IsUrl</c> alone silently prints raw GUIDs the day a second special type arrives.</summary>
    public bool ShowPlainText => !IsUrl && !IsDocumentReference;

    /// <summary>The resolved targets, for the reference template. Server-resolved, so a target the caller
    /// may not open arrives already stripped of its name and address.</summary>
    public IReadOnlyList<IndexFieldTargetViewModel> Targets { get; init; } = [];

    /// <summary>How to reveal a target in the workbench. Null where the pane showing this field cannot
    /// navigate — the check-out tab has no tree to reveal into — which the template honours by leaving the
    /// row as plain text rather than offering a link that would do nothing.</summary>
    public Func<Guid, Task>? OpenDocument { get; init; }

    [RelayCommand]
    private async Task OpenTarget(IndexFieldTargetViewModel? target)
    {
        if (target is { CanOpen: true } && OpenDocument is { } open)
        {
            await open(target.Id);
        }
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        // Only what the field's own validation admits (absolute http/https, core ADR 0755) — and re-checked
        // here, because a link that shells out is a link worth double-checking.
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
    }

    /// <summary>The read row for a served field group, with type-aware rendering.</summary>
    /// <remarks>
    /// One factory for every tab that shows index data, because the rendering rule is easy to get wrong in
    /// one copy and invisible when you do: a DateTime value is a WIRE instant
    /// (<c>2026-09-04T12:30:00+00:00</c>), and shown raw it reads as a date with debris — the pane bug the
    /// owner reported. Rendered as the local wall clock via the shared Presentation arithmetic.
    /// </remarks>
    /// <param name="openDocument">How to reveal a DocumentReference target, or null where this pane cannot
    /// navigate. REQUIRED rather than settable, so the compiler enumerates the call sites: a forgotten
    /// callback disables a visible link, and #854's lesson is that nothing else enumerates them for you.</param>
    public static IndexFieldViewModel From(Services.DocumentsClient.IndexField field, Func<Guid, Task>? openDocument) => new()
    {
        FieldName = field.FieldName,
        Values = string.Join(", ", field.Values.Select(v =>
            field.DataType == "DateTime" ? SimplArchive.Presentation.IndexInstant.Display(v) : v)),
        IsUrl = field.DataType == "Url",
        UrlValues = field.DataType == "Url" ? field.Values : [],
        IsDocumentReference = field.DataType == "DocumentReference",
        Targets = field.DataType == "DocumentReference"
            // Openable means BOTH halves: the server advertised the address, and this pane can navigate. A
            // link drawn on the first alone would be an affordance that does nothing when clicked.
            ? field.Targets.Select(t => new IndexFieldTargetViewModel(t, openDocument is not null)).ToList()
            : [],
        OpenDocument = openDocument,
    };
}

/// <summary>One target row of a DocumentReference field.</summary>
/// <remarks>
/// The unavailable case is drawn from the SERVER's answer, not re-derived here: no name and no address means
/// the reader may not open it (ADR 0543), and the id it still carries is not something to show a person.
/// </remarks>
public sealed class IndexFieldTargetViewModel(Services.DocumentsClient.IndexFieldTarget target, bool paneCanNavigate)
{
    public Guid Id { get; } = target.Id;

    public bool CanOpen { get; } = target.CanOpen && paneCanNavigate;

    /// <summary>The name where the server gave one, the unavailable text where it did not. Note this reads
    /// the SERVER's answer (<c>target.CanOpen</c>), not <see cref="CanOpen"/>: a pane that cannot navigate
    /// still shows the name it was given — it just does not offer to open it.</summary>
    public string Display { get; } = target.CanOpen && target.Name is { Length: > 0 }
        ? target.Name
        : SimplArchive.Localization.Strings.Get("IdxTargetUnavailable");
}

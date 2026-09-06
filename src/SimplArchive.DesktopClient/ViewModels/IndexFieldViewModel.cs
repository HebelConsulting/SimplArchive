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
    public static IndexFieldViewModel From(Services.DocumentsClient.IndexField field) => new()
    {
        FieldName = field.FieldName,
        Values = string.Join(", ", field.Values.Select(v =>
            field.DataType == "DateTime" ? SimplArchive.Presentation.IndexInstant.Display(v) : v)),
        IsUrl = field.DataType == "Url",
        UrlValues = field.DataType == "Url" ? field.Values : [],
    };
}

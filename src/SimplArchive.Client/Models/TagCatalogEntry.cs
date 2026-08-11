using SimplArchive.Client.Hypermedia;

namespace SimplArchive.Client.Models;

/// <summary>
/// One tag in the tenant's catalogue, with the addresses its own row advertised (ADR 0543).
/// </summary>
/// <remarks>
/// Shared because the Tags tab manages the catalogue while the shell reads it — autocomplete in the index
/// pane, and chip colours on list rows (ADR 0558).
/// </remarks>
public record TagCatalogEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = ""; public string? Color { get; set; }
    public List<LinkResponse> Links { get; set; } = [];

    // self (rename/recolour), retire, merge — the catalog lists only LIVE tags, so `unretire` has no row
    // here and is correctly absent (issue #416).
    public string? Href(string rel) => Links.FirstOrDefault(l => l.Rel == rel)?.Href.TrimStart('/');
}

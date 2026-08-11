namespace SimplArchive.Client.Models;

/// <summary>The tag catalogue listing: the live tags, and whether the caller may manage them.</summary>
public record TagsResponse
{
    public List<string> Tags { get; set; } = [];

    public List<TagCatalogEntry> Catalog { get; set; } = [];

    public bool CanManage { get; set; }
}

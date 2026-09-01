namespace SimplArchive.Client.Models;

/// <summary>A tenant's sensitivity label — its rank, its chip colour, and whether it watermarks a preview.</summary>
/// <remarks>
/// Shared because two surfaces read the same list: the Repositories detail pane offers it as a picker, and the
/// Users &amp; groups tab maps a clearance rank onto a label name (ADR 0558). Fetched through
/// <see cref="SimplArchive.Client.Services.SensitivityLabelCatalog"/> so neither owns the loader.
/// </remarks>
public record SensitivityLabel
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Higher outranks lower; a caller's clearance is compared against this.</summary>
    public int Rank { get; set; }

    public string? Color { get; set; }

    /// <summary>When set, a preview of a document carrying this label is watermarked.</summary>
    public bool Watermark { get; set; }

    /// <summary>Retired labels stay readable on existing documents but are not offered for new ones.</summary>
    public bool Retired { get; set; }
}

/// <summary>The labels plus whether the caller may edit them.</summary>
public record SensitivityLabelsResponse
{
    public List<SensitivityLabel> Labels { get; set; } = [];

    public bool CanManage { get; set; }
}

namespace SimplArchive.Client.Hypermedia;

/// <summary>
/// One link relation as the API states it (ADR 0543): a name and the address it names.
/// </summary>
/// <remarks>
/// Public and shared because every screen reads links, and the decomposition of the workbench page into
/// components (ADR 0558) would otherwise need a private copy of this record per component — N copies of the
/// one type that carries the API's compatibility surface, which is exactly what drifts.
/// </remarks>
public record LinkResponse
{
    public string Rel { get; set; } = string.Empty;

    public string Href { get; set; } = string.Empty;
}

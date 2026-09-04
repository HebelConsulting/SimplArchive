namespace SimplArchive.DesktopClient.ViewModels;

// A row in the index-data pane: a field name and its (joined) value(s).
public sealed class IndexFieldViewModel
{
    public required string FieldName { get; init; }

    public required string Values { get; init; }

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
    };
}

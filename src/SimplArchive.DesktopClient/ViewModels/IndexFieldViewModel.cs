namespace SimplArchive.DesktopClient.ViewModels;

// A row in the index-data pane: a field name and its (joined) value(s).
public sealed class IndexFieldViewModel
{
    public required string FieldName { get; init; }

    public required string Values { get; init; }
}

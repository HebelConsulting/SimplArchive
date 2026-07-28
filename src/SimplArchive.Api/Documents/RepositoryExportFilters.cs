namespace SimplArchive.Api.Documents;

// Which versions of a document to export (ADR "Repository export"). ActiveOnly resolves the workflow
// "latest-Released-as-current" version (the version an end user sees as current) — which may not be the
// highest version number, since a newer version can still be in review (gated).
public enum ExportVersionSelection
{
    All,
    ActiveOnly,
}

// The filters an export applies while walking the subtree (ADR "Repository export"). All optional; a null
// bound means "unbounded on that side". CreatedBy matches a version's creator name (User.DisplayName /
// User email / ServiceAccount.Name), case-insensitive substring.
public sealed record RepositoryExportFilters(
    DateOnly? DocumentDateFrom,
    DateOnly? DocumentDateTo,
    DateTimeOffset? FiledFrom,
    DateTimeOffset? FiledTo,
    ExportVersionSelection Versions,
    string? CreatedBy);

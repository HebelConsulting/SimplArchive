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
// TimeZoneId: which zone DocumentDateFrom/To are expressed in (ADR 0801, #1256). Null reads them as UTC, which
// is what a scripted caller with no preference and no X-Time-Zone header gets — and is exactly the behaviour
// that existed before, so such a caller sees no change. It is deliberately NOT defaulted to the server's zone:
// that would make every export inherit whatever zone the container happens to run in.
public sealed record RepositoryExportFilters(
    DateOnly? DocumentDateFrom,
    DateOnly? DocumentDateTo,
    DateTimeOffset? FiledFrom,
    DateTimeOffset? FiledTo,
    ExportVersionSelection Versions,
    string? CreatedBy,
    string? TimeZoneId = null);

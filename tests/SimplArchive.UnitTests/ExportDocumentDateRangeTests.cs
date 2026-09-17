using SimplArchive.Api.Documents;
using SimplArchive.Domain.Documents;

namespace SimplArchive.UnitTests;

// Which versions an export's document-date filter keeps, read in the CALLER's zone (#1256, ADR 0801).
//
// The end-to-end tests prove the wiring — that the controller resolves a zone and the archive comes out right.
// These pin the two halves that are easy to get quietly wrong and that no green export would reveal: a
// date-only version must NOT move, and the upper bound must INCLUDE its own day. Both failures look like
// "the export is a bit short", reported as success.
public class ExportDocumentDateRangeTests
{
    private const string Zurich = "Europe/Zurich";

    private static DocumentVersion Version(DateOnly date, TimeOnly? time = null) =>
        new() { Id = Guid.NewGuid(), ObjectKey = "k", DocumentDate = date, DocumentTime = time };

    private static RepositoryExportFilters Range(DateOnly? from, DateOnly? to, string? zone) =>
        new(from, to, null, null, ExportVersionSelection.All, null, zone);

    [Fact]
    public void A_timed_version_belongs_to_the_day_it_falls_on_IN_THE_CALLERS_ZONE()
    {
        // 22:30 UTC on the 15th is 00:30 on the 16th in Zurich — the case the whole change exists for, and the
        // one a verbatim comparison gets backwards in both directions.
        var late = Version(new DateOnly(2026, 7, 15), new TimeOnly(22, 30));

        Assert.True(WithinDocumentDateRange(late, Range(new DateOnly(2026, 7, 16), new DateOnly(2026, 7, 16), Zurich)));
        Assert.False(WithinDocumentDateRange(late, Range(new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), Zurich)));

        // And with no zone, the behaviour that existed before — which is what a scripted caller still gets.
        Assert.False(WithinDocumentDateRange(late, Range(new DateOnly(2026, 7, 16), new DateOnly(2026, 7, 16), zone: null)));
        Assert.True(WithinDocumentDateRange(late, Range(new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), zone: null)));
    }

    [Fact]
    public void A_DATE_ONLY_version_never_moves_however_the_caller_reads_the_bounds()
    {
        // THE DELIBERATE EXCEPTION. Somebody chose the 15th; it is the 15th in every zone. An implementation
        // that converted "everything" would drop this from an export for the 15th — inventing a bug in order
        // to fix one that was never there.
        var filed = Version(new DateOnly(2026, 7, 15));

        Assert.True(WithinDocumentDateRange(filed, Range(new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), Zurich)));
        Assert.False(WithinDocumentDateRange(filed, Range(new DateOnly(2026, 7, 16), new DateOnly(2026, 7, 16), Zurich)));
    }

    [Fact]
    public void The_upper_bound_INCLUDES_its_own_day()
    {
        // "to = the 16th" means through the END of the 16th. A half-open window whose end is the START of that
        // day silently drops everything filed on the last day of the range.
        var lateOnTheDay = Version(new DateOnly(2026, 7, 16), new TimeOnly(21, 0));   // 23:00 in Zurich, still the 16th

        Assert.True(WithinDocumentDateRange(lateOnTheDay, Range(new DateOnly(2026, 7, 16), new DateOnly(2026, 7, 16), Zurich)));
    }

    [Fact]
    public void The_lower_bound_INCLUDES_its_own_day_from_midnight()
    {
        // The mirror: 22:30 UTC on the 15th is 00:30 on the 16th, the FIRST half-hour of the range's first day.
        var justAfterMidnight = Version(new DateOnly(2026, 7, 15), new TimeOnly(22, 30));

        Assert.True(WithinDocumentDateRange(justAfterMidnight, Range(new DateOnly(2026, 7, 16), null, Zurich)));

        // And 22:00 UTC — 00:00 exactly — is in, because the lower bound is inclusive.
        Assert.True(WithinDocumentDateRange(
            Version(new DateOnly(2026, 7, 15), new TimeOnly(22, 0)), Range(new DateOnly(2026, 7, 16), null, Zurich)));

        // One minute earlier is the previous day, and out.
        Assert.False(WithinDocumentDateRange(
            Version(new DateOnly(2026, 7, 15), new TimeOnly(21, 59)), Range(new DateOnly(2026, 7, 16), null, Zurich)));
    }

    [Fact]
    public void An_unbounded_filter_keeps_everything_and_asks_no_questions()
    {
        // The overwhelmingly common case — no date filter at all — must not depend on a zone being resolvable,
        // or every export would start paying for a lookup nobody asked for.
        Assert.True(WithinDocumentDateRange(Version(new DateOnly(2026, 7, 15), new TimeOnly(22, 30)), Range(null, null, Zurich)));
        Assert.True(WithinDocumentDateRange(Version(new DateOnly(1999, 1, 1)), Range(null, null, zone: null)));
    }

    [Fact]
    public void An_unknown_zone_falls_back_to_the_literal_comparison_rather_than_failing_the_export()
    {
        // A preference outlives the host it was set on: a zone can be renamed, and a server's database can be
        // older than the one the preference was chosen on. A stale preference should cost the caller the
        // conversion, not the whole archive.
        var late = Version(new DateOnly(2026, 7, 15), new TimeOnly(22, 30));

        Assert.True(WithinDocumentDateRange(late, Range(new DateOnly(2026, 7, 15), new DateOnly(2026, 7, 15), "Mars/Olympus_Mons")));
    }

    private static bool WithinDocumentDateRange(DocumentVersion version, RepositoryExportFilters filters) =>
        RepositoryExporter.WithinDocumentDateRange(version, filters);
}

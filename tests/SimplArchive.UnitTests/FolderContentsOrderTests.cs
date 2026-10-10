using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// How every client lists a folder (desktop, web, phone): folders first and alphabetical, then documents in the
// folder's stored order. The phone listed the server's creation order until 2026-10-10.
public sealed class FolderContentsOrderTests
{
    private sealed record Row(string Name, bool IsFolder, DateOnly? Date = null, TimeOnly? Time = null, DateTimeOffset? Filed = null);

    private static List<string> Arrange(int sortOrder, params Row[] rows) =>
        [.. FolderContentsOrder.Arrange(rows, sortOrder, r => new FolderContentsOrder.Keys(r.IsFolder, r.Name, r.Date, r.Time, r.Filed)).Select(r => r.Name)];

    [Fact]
    public void Folders_come_first_alphabetically_whatever_the_listing_order() =>
        Assert.Equal(["acme Corp", "MyCountry Telekom", "Zeta"],
            Arrange(0, new("MyCountry Telekom", true), new("Zeta", true), new("acme Corp", true)));

    [Fact]
    public void Documents_follow_by_document_date_then_time_then_name_by_default() =>
        Assert.Equal(["Folder", "B", "A", "C"],
            Arrange(1,
                new("C", false, new DateOnly(2026, 3, 1)),
                new("A", false, new DateOnly(2026, 1, 1), new TimeOnly(10, 0)),
                new("Folder", true),
                new("B", false, new DateOnly(2026, 1, 1), new TimeOnly(9, 0))));

    [Fact]
    public void By_name_and_by_filing_time_as_the_folder_says_and_an_unknown_order_falls_back_to_name()
    {
        var early = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        Row[] rows = [new("b", false, Filed: early), new("A", false, Filed: early.AddDays(1))];

        Assert.Equal(["A", "b"], Arrange(0, rows));
        Assert.Equal(["b", "A"], Arrange(2, rows));
        Assert.Equal(["A", "b"], Arrange(7, rows));
    }
}

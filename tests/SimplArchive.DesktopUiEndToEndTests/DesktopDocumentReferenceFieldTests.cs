using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of the DocumentReference index field (ADR 0773). The desktop is canonical (ADR 0511), so
// the rendering rules are pinned here and the web mirrors them.
//
// View-model level, deliberately: what the pane draws is decided entirely by the server's answer plus one
// local fact — whether THIS pane can navigate — and the interesting cases are all about a target that cannot
// be opened, for either of the two different reasons.
public class DesktopDocumentReferenceFieldTests
{
    private static DocumentsClient.IndexFieldTarget Target(string? name, bool advertised) =>
        new(Guid.NewGuid(), name, advertised ? [new DocumentsClient.IndexFieldLink("document", "https://example.test/api/documents/x")] : []);

    private static IndexFieldViewModel Field(DocumentsClient.IndexFieldTarget target, bool canNavigate) =>
        IndexFieldViewModel.From(
            new DocumentsClient.IndexField("Flight", [target.Id.ToString()], "DocumentReference") { Targets = [target] },
            canNavigate ? _ => Task.CompletedTask : null);

    [Fact]
    public void A_resolved_target_shows_its_name_and_offers_to_open_it()
    {
        var row = Field(Target("HB-PHG 2026-09-09", advertised: true), canNavigate: true);

        Assert.True(row.IsDocumentReference);
        Assert.False(row.ShowPlainText);   // never the raw id
        var target = Assert.Single(row.Targets);
        Assert.Equal("HB-PHG 2026-09-09", target.Display);
        Assert.True(target.CanOpen);
    }

    // The server withheld the name and the address, which is it saying "not available to you, here, now"
    // (ADR 0543). The pane must not improve on that by showing the id it still holds.
    [Fact]
    public void A_target_the_server_withheld_is_neither_named_nor_openable()
    {
        var target = Target(name: null, advertised: false);
        var row = Field(target, canNavigate: true);

        var shown = Assert.Single(row.Targets);
        Assert.False(shown.CanOpen);
        Assert.NotEqual(target.Id.ToString(), shown.Display);
        Assert.False(string.IsNullOrWhiteSpace(shown.Display));   // it says something, rather than nothing
    }

    // The second reason a target cannot be opened, and it must NOT look like the first: the Check-out tab has
    // no tree to reveal into, so it shows the name it was given and simply does not offer the link. Drawing
    // the unavailable text here would tell the reader their rights are missing when they are not.
    [Fact]
    public void A_pane_that_cannot_navigate_still_shows_the_name()
    {
        var row = Field(Target("HB-PHG 2026-09-09", advertised: true), canNavigate: false);

        var target = Assert.Single(row.Targets);
        Assert.Equal("HB-PHG 2026-09-09", target.Display);
        Assert.False(target.CanOpen);
    }

    // The plain-text row is what every other field type uses, and it must stay switched off for BOTH special
    // types — a template hiding on "not a Url" alone would print raw GUIDs.
    [Theory]
    [InlineData("Text", true)]
    [InlineData("DateTime", true)]
    [InlineData("Url", false)]
    [InlineData("DocumentReference", false)]
    public void Only_ordinary_types_render_as_plain_text(string dataType, bool plain)
    {
        var row = IndexFieldViewModel.From(
            new DocumentsClient.IndexField("F", ["x"], dataType), openDocument: null);

        Assert.Equal(plain, row.ShowPlainText);
    }
}

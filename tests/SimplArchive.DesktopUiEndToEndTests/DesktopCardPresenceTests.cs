using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The card leaving the reader discards what it decrypted (#1353 decision 2, ADR 0832).
//
// The watcher's CHECK is separated from its timer precisely so this can be driven without waiting five seconds
// per assertion — and, more importantly, so the edge behaviour is pinned. Edge-triggering is the part that is
// easy to get wrong and invisible when wrong: a level check would fire on every tick for the very many machines
// that simply never had a card, which would close a user's document over and over for no reason.
public class DesktopCardPresenceTests
{
    [Fact]
    public void Removal_fires_once_after_a_card_was_actually_seen()
    {
        var removals = 0;
        var watcher = new CardPresenceWatcher(() => removals++);
        var present = true;

        watcher.Check(() => present);   // the card is in
        Assert.Equal(0, removals);

        present = false;
        watcher.Check(() => present);   // one absent reading is NOT a removal
        Assert.Equal(0, removals);
        watcher.Check(() => present);   // the second confirms it
        Assert.Equal(1, removals);

        // And NOT again on every subsequent tick, which is what an edge is for.
        watcher.Check(() => present);
        watcher.Check(() => present);
        Assert.Equal(1, removals);
    }

    [Fact]
    public void A_machine_that_never_had_a_card_is_never_told_one_was_removed()
    {
        // The common case by far. Firing here would close an open document on every installation that does not
        // use cards at all — a worse defect than the one the watcher exists to fix.
        var removals = 0;
        var watcher = new CardPresenceWatcher(() => removals++);

        watcher.Check(() => false);
        watcher.Check(() => false);
        watcher.Check(() => false);

        Assert.Equal(0, removals);
    }

    [Fact]
    public void Re_inserting_arms_it_again()
    {
        // Someone who pulls their card to walk to a printer and comes back must be protected a second time.
        var removals = 0;
        var watcher = new CardPresenceWatcher(() => removals++);
        var present = true;

        watcher.Check(() => present);
        present = false;
        watcher.Check(() => present);
        watcher.Check(() => present);
        Assert.Equal(1, removals);

        present = true;
        watcher.Check(() => present);
        present = false;
        watcher.Check(() => present);
        watcher.Check(() => present);

        Assert.Equal(2, removals);
    }
}

// Discarding what the card decrypted, which is what a removal actually does (#1353 decision 2, ADR 0832).
//
// Deliberately asserted over EVERY preview surface rather than the Repositories one. The lists that feed those
// surfaces have drifted before — a tab in one list and not the other showed a blank pane and nobody noticed
// (DesktopPreviewWiringTests records it) — and here the same drift would leave a decrypted page on screen after
// the card was pulled, which is precisely the thing this is for.
public class DesktopDiscardDecryptedContentTests
{
    [Fact]
    public void Every_preview_surface_is_cleared_and_says_why()
    {
        const string Because = "the card was removed";
        var vm = new SimplArchive.DesktopClient.ViewModels.MainWindowViewModel();

        // Put each surface into a state that must not survive: a placeholder that is not ours, and a page count
        // standing in for rendered bitmaps.
        foreach (var preview in vm.PreviewSurfaces)
        {
            preview.Reset("something else entirely");
            preview.HasPreviewPages = true;
        }

        vm.DiscardDecryptedContent(Because);

        Assert.All(vm.PreviewSurfaces, preview =>
        {
            Assert.Equal(Because, preview.PreviewPlaceholder);
            Assert.False(preview.HasPreviewPages);
            Assert.Empty(preview.PreviewPages);

            // Find-in-document runs over the decrypted text, so it must go with it — otherwise a user can still
            // search the contents of a document the card was pulled on.
            Assert.False(preview.CanFindInDocument);
            Assert.Null(preview.PreviewText);
        });
    }

    [Fact]
    public void A_SINGLE_absent_reading_does_not_close_the_document()
    {
        // The live failure this guards: another process on the same machine talked to the same token, this
        // client's next reading came back empty, and a card that had never left the reader was treated as
        // removed — closing the user's open document. The counter must reset on any sighting.
        var removals = 0;
        var watcher = new CardPresenceWatcher(() => removals++);
        var present = true;

        watcher.Check(() => present);
        present = false;
        watcher.Check(() => present);   // a blip
        present = true;
        watcher.Check(() => present);   // still there
        present = false;
        watcher.Check(() => present);   // another blip, but the counter was reset

        Assert.Equal(0, removals);
    }
}

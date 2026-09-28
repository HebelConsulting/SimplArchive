using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Taking the card out closes what the card DECRYPTED, and nothing else (#1450, ADR 0832).
//
// The defect this guards was reported from the kiosk, on the public demo tenant — which has no encryption mode
// at all, so no document there has ever been enveloped. Removing a YubiKey blanked the document on screen and
// replaced it with the "card removed" placeholder, which is the opposite of the reassurance the feature exists
// to give: it told a reader their plaintext document had been closed for a security reason that did not apply.
//
// WHY IT SURVIVED. The wiring in App.axaml.cs carried the correct rule in prose — "metadata browsing is
// unaffected because none of it was ever enveloped" — while DiscardDecryptedContent reset EVERY preview surface
// unconditionally. A comment stating the intent is not a test of it, and nothing else could notice: the blanking
// looks identical whether it was right or wrong, and only a reader on a non-encrypted tenant ever sees the
// difference.
[Collection(UiCollection.Name)]
public class DesktopCardRemovalScopeTests
{
    [Fact]
    public void A_preview_that_was_never_enveloped_survives_the_card_leaving()
    {
        var vm = new MainWindowViewModel();
        var plaintext = vm.PreviewSurfaces[0];

        // What the demo tenant produces: content read straight from object storage, never an envelope.
        plaintext.ContentWasEnveloped = false;
        plaintext.Reset(null);

        vm.DiscardDecryptedContent("Card removed — content closed.");

        Assert.Null(plaintext.PreviewPlaceholder);
    }

    [Fact]
    public void A_preview_holding_decrypted_content_IS_closed()
    {
        var vm = new MainWindowViewModel();
        var enveloped = vm.PreviewSurfaces[0];

        enveloped.ContentWasEnveloped = true;
        enveloped.Reset(null);

        // Reset() clears the flag — nothing on display means nothing enveloped on display — so it is set back
        // here to stand for a surface that has just finished rendering a decrypted document.
        enveloped.ContentWasEnveloped = true;

        vm.DiscardDecryptedContent("Card removed — content closed.");

        Assert.Equal("Card removed — content closed.", enveloped.PreviewPlaceholder);
    }

    [Fact]
    public void One_enveloped_surface_does_not_close_the_others()
    {
        // The case that decides SURFACE-level over SESSION-level. A reader on a strict tenant opens an enveloped
        // document in one pane and an ordinary one in another; pulling the card must close only the first. A
        // session-wide "has anything ever been decrypted?" flag would have closed both — the same wrong message,
        // just rarer than the reported bug.
        var vm = new MainWindowViewModel();
        var enveloped = vm.PreviewSurfaces[0];
        var plaintext = vm.PreviewSurfaces[1];

        enveloped.Reset(null);
        plaintext.Reset(null);
        enveloped.ContentWasEnveloped = true;
        plaintext.ContentWasEnveloped = false;

        vm.DiscardDecryptedContent("Card removed — content closed.");

        Assert.Equal("Card removed — content closed.", enveloped.PreviewPlaceholder);
        Assert.Null(plaintext.PreviewPlaceholder);
    }

    [Fact]
    public void Resetting_a_surface_clears_the_flag_so_a_later_removal_leaves_it_alone()
    {
        // Otherwise the flag outlives the content it describes: a pane that once showed an enveloped document
        // and has since been reset holds nothing, and closing "it" on a later card removal would blank whatever
        // has been put there since.
        var vm = new MainWindowViewModel();
        var surface = vm.PreviewSurfaces[0];

        surface.ContentWasEnveloped = true;
        surface.Reset(null);

        Assert.False(surface.ContentWasEnveloped);

        vm.DiscardDecryptedContent("Card removed — content closed.");
        Assert.Null(surface.PreviewPlaceholder);
    }
}

// The card coming BACK puts the document back (#1453).
//
// Reported after the removal half shipped: pulling the token closed the preview and showed "The card was
// removed, so the open document was closed. Put it back to read again." — and putting it back did nothing. The
// reader had to select a DIFFERENT document to be re-prompted for the PIN before they could read again.
//
// That is worse than not promising it. The placeholder tells the reader exactly what to do, and the client then
// ignored them doing it, which reads as the card not being detected at all.
[Collection(UiCollection.Name)]
public class DesktopCardReturnTests
{
    private static Preview APreview() => new("https://x/preview", false, "https://x/download", null, null, ".pdf");

    [Fact]
    public void The_watcher_reports_a_return_only_after_a_removal_it_acted_on()
    {
        var removed = 0;
        var returned = 0;
        using var watcher = new CardPresenceWatcher(() => removed++, () => returned++);

        // A card that simply sits in the reader must not look like a return, however many times it is seen.
        watcher.Check(() => true);
        watcher.Check(() => true);
        Assert.Equal(0, removed);
        Assert.Equal(0, returned);

        // Removal needs two consecutive absences (a single blip is not a removal).
        watcher.Check(() => false);
        Assert.Equal(0, removed);
        watcher.Check(() => false);
        Assert.Equal(1, removed);
        Assert.Equal(0, returned);

        // Back again — once.
        watcher.Check(() => true);
        Assert.Equal(1, returned);

        watcher.Check(() => true);
        Assert.Equal(1, returned);
    }

    [Fact]
    public void A_surface_closed_by_the_card_is_queued_for_restore()
    {
        var vm = new MainWindowViewModel();
        var surface = vm.PreviewSurfaces[0];

        surface.ContentWasEnveloped = true;
        typeof(PreviewViewModel).GetProperty("LastRendered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(surface, APreview());

        vm.DiscardDecryptedContent("closed");

        Assert.Equal(1, vm.PendingCardRestoreCount);
    }

    [Fact]
    public void A_surface_that_was_never_enveloped_is_not_queued()
    {
        var vm = new MainWindowViewModel();
        var surface = vm.PreviewSurfaces[0];

        surface.ContentWasEnveloped = false;
        typeof(PreviewViewModel).GetProperty("LastRendered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(surface, APreview());

        vm.DiscardDecryptedContent("closed");

        Assert.Equal(0, vm.PendingCardRestoreCount);
    }

    [Fact]
    public async Task The_queue_is_emptied_by_a_restore_so_a_later_return_does_not_replay_it()
    {
        var vm = new MainWindowViewModel();
        var surface = vm.PreviewSurfaces[0];

        surface.ContentWasEnveloped = true;
        typeof(PreviewViewModel).GetProperty("LastRendered",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(surface, APreview());

        vm.DiscardDecryptedContent("closed");
        Assert.Equal(1, vm.PendingCardRestoreCount);

        await vm.RestoreContentClosedByCardRemovalAsync();

        // Otherwise a second insertion re-renders a document the reader may have navigated away from.
        Assert.Equal(0, vm.PendingCardRestoreCount);
    }
}

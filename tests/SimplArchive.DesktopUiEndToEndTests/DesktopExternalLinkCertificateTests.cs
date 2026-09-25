using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.Localization;

namespace SimplArchive.UiEndToEndTests;

// The create-link dialog's half of ADR 0827 (#1390): the strict tier insists on a recipient, and says so
// before the round trip rather than after it.
//
// View-model level and server-free, because that is exactly the boundary being pinned. The SERVER's refusal is
// proven in the E2E suite and stands whatever a client believes; what matters here is the difference between a
// field the sharer can still fill in and an error about one — a dialog that let the request go and reported
// the 409 would be correct and useless.
//
// The certificate DESCRIPTION is deliberately not tested here: it is no longer a local parse. Blazor WASM
// cannot parse an X.509 certificate at all (PlatformNotSupportedException, measured), so both clients ask the
// server and there is one implementation of that question rather than two that can disagree. It is covered
// where it now lives — the E2E suite, against the real endpoint.
public class DesktopExternalLinkCertificateTests
{
    [Fact]
    public async Task The_strict_tier_blocks_creation_until_a_recipient_is_named()
    {
        var dialog = Dialog();
        dialog.RequiresRecipientCertificate = true;

        await dialog.CreateCommand.ExecuteAsync(null);

        // The dialog's own words, not a server's. Nothing was sent: the api client here points at no server,
        // so a request would have surfaced as a connection failure rather than this message.
        Assert.Equal(Strings.Get("ExtLinkCertRequired"), dialog.Status);
    }

    [Fact]
    public void An_ordinary_tenant_asks_for_nothing()
    {
        // The contrast that stops the check above being "always refuse": outside the strict tier a link needs
        // no recipient, and the field stays an option rather than a demand.
        Assert.False(Dialog().RequiresRecipientCertificate);
    }

    /// <summary>The dialog with no server behind it — the paths under test make no request.</summary>
    private static ExternalLinksDialogViewModel Dialog() =>
        new(new SimplArchiveApiClient("no-token"), "/api/documents/x/external-links", "doc", false);
}

using SimplArchive.Localization;
using SimplArchive.Presentation;

namespace SimplArchive.Client.Pages;

// What the workbench says when this tenant's content is ENVELOPED to the reader's own key and the browser
// therefore cannot open it (#1352, ADR 0830). Its own partial rather than lines in the shell, because the shell
// is being decomposed (ADR 0558) and may only get smaller — the guard that caught this is the reason the split
// happens at the moment the code is written rather than in a later tidy-up.
//
// A browser is out by construction, not by omission: WebCrypto has no PKCS#11 bridge, client-certificate TLS
// authenticates the connection rather than decrypting a CMS blob, and the legacy hooks are long gone. Since the
// tier's claim is that the key never leaves the card, the honest answer is to say so and name the desktop
// client — never to fall back to a plaintext door, which would mean an attacker who controls the API need only
// decline to envelope (#1351).
public partial class Home
{
    // Three facts, three sentences, and the CHOICE is shared (PreviewEmptyReasons) rather than written here:
    // "no preview available" is right for a rendition that could not be produced and wrong for the strict tier,
    // where the bytes exist and are readable by their owner. The two are indistinguishable from the links alone,
    // so the server states the tier and the shared rule turns the pair into one answer.
    private string PreviewEmptyText => Strings.Get(
        PreviewEmptyReasons.KeyOf(
            PreviewEmptyReasons.For(hasSelection: _selectedItem is not null, Detail.SysContentIsEnveloped)));

    // The disabled Download button already says "you cannot"; on a strict tenant it cannot say WHY, and the
    // reason is not that the document is broken. The tooltip is the ribbon's own explaining channel (icon +
    // title, ADR 0576), so it carries the explanation and names the desktop client — while aria-label stays the
    // action's name, which is what a screen reader needs to announce.
    private string DownloadTooltip =>
        _downloadUrl is null && Detail.SysContentIsEnveloped
            ? Strings.Get("PreviewEnvelopedUseDesktop")
            : Strings.Get("Download");
}

using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// Why a preview pane is empty — three facts needing three sentences (#1352, ADR 0830).
//
// The case worth a test is the third one. On a strict-tier tenant the bytes exist and are readable by their
// owner; they simply cannot be opened IN A BROWSER, because WebCrypto has no PKCS#11 bridge and so a page cannot
// decrypt an envelope addressed to a card-held key. Saying "no preview available" there sends the reader looking
// for a broken document instead of to the desktop client — and the two are indistinguishable from the links
// alone, since a strict version carries neither `download` nor `preview`, exactly like a failed rendition.
public class PreviewEmptyReasonTests
{
    [Fact]
    public void Nothing_selected_wins_over_a_stale_enveloped_flag()
    {
        // Order matters: the flag describes the PREVIOUS subject until the next load replaces it, and an idle
        // pane must not accuse a document that is not there (ADR 0559 — the state is cleared as well, but a
        // rule that depended on that being perfect would be a rule waiting to be wrong).
        Assert.Equal(
            PreviewEmptyReason.NothingSelected,
            PreviewEmptyReasons.For(hasSelection: false, contentIsEnveloped: true));
    }

    [Fact]
    public void An_enveloping_tenant_is_named_rather_than_reported_as_a_missing_preview()
    {
        Assert.Equal(
            PreviewEmptyReason.EnvelopedUseDesktop,
            PreviewEmptyReasons.For(hasSelection: true, contentIsEnveloped: true));
    }

    [Fact]
    public void An_ordinary_tenants_unrenderable_document_still_says_no_preview()
    {
        // The anti-vacuous half: a rule that answered "use the desktop client" to everything would pass the
        // test above, and would be wrong on every tenant that is not in the tier.
        Assert.Equal(
            PreviewEmptyReason.NoPreview,
            PreviewEmptyReasons.For(hasSelection: true, contentIsEnveloped: false));
    }

    [Theory]
    [InlineData(PreviewEmptyReason.NothingSelected, "NoDocSelected")]
    [InlineData(PreviewEmptyReason.EnvelopedUseDesktop, "PreviewEnvelopedUseDesktop")]
    [InlineData(PreviewEmptyReason.NoPreview, "NoPreview")]
    public void Each_reason_names_a_key_that_exists_in_every_language(PreviewEmptyReason reason, string expectedKey)
    {
        var key = PreviewEmptyReasons.KeyOf(reason);
        Assert.Equal(expectedKey, key);

        // And the key RESOLVES. A reason whose sentence is missing renders as the key itself — which is how a
        // user comes to read "PreviewEnvelopedUseDesktop" in a pane, and it has happened here before (a
        // duplicate resx key is only a warning, MSB3568).
        var text = SimplArchive.Localization.Strings.Get(key);
        Assert.NotEqual(key, text);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}

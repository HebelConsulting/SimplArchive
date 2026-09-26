namespace SimplArchive.Presentation;

/// <summary>
/// Why a preview pane has nothing to show — three DIFFERENT facts that need three different sentences.
/// </summary>
/// <remarks>
/// <para>
/// Shared rather than decided per client because the third case is new (#1352) and easy to get wrong in the
/// direction that costs a user their afternoon: on a strict-tier tenant the bytes exist, are readable by their
/// owner, and simply cannot be opened <b>in a browser</b> — WebCrypto has no PKCS#11 bridge, so a page cannot
/// decrypt an envelope addressed to a card-held key. Reporting "no preview available" there sends the reader
/// looking for a broken document instead of to the desktop client.
/// </para>
/// <para>
/// And the cases are indistinguishable from the links alone: a strict version carries no <c>download</c> and no
/// <c>preview</c> rel, which is byte-for-byte what a version whose rendition failed looks like. ADR 0543 makes
/// absence mean "not available to you, here, now" — true of both, for reasons a reader needs told apart. So the
/// server states the tier (<c>contentIsEnveloped</c>) and this function turns the two facts into one answer.
/// </para>
/// </remarks>
public enum PreviewEmptyReason
{
    /// <summary>Nothing is selected — the pane is idle rather than unable.</summary>
    NothingSelected,

    /// <summary>Selected, but this tenant envelopes its content to the reader's own key, which a browser cannot
    /// open. A permanent condition with a remedy: the desktop client.</summary>
    EnvelopedUseDesktop,

    /// <summary>Selected, and no rendition could be produced — the long-standing case.</summary>
    NoPreview,
}

/// <summary>Chooses the reason, so every surface chooses it the same way.</summary>
public static class PreviewEmptyReasons
{
    /// <param name="hasSelection">Whether a document is selected at all.</param>
    /// <param name="contentIsEnveloped">
    /// The server's answer for the SELECTED document's tenant (<c>contentIsEnveloped</c>).
    /// </param>
    /// <remarks>
    /// Order matters and is the whole content of this function. "Nothing selected" wins, because a stale
    /// enveloped flag from a previous subject must not accuse an empty pane (ADR 0559 — clear the value too, but
    /// do not rely on that alone). And enveloping wins over "no preview", because it is the more specific and
    /// the more permanent of the two: a tenant that envelopes will never render in a browser, whereas a failed
    /// rendition may succeed on the next attempt.
    /// </remarks>
    public static PreviewEmptyReason For(bool hasSelection, bool contentIsEnveloped) =>
        !hasSelection ? PreviewEmptyReason.NothingSelected
        : contentIsEnveloped ? PreviewEmptyReason.EnvelopedUseDesktop
        : PreviewEmptyReason.NoPreview;

    /// <summary>The localisation key each reason renders, so the two clients cannot drift on the wording.</summary>
    public static string KeyOf(PreviewEmptyReason reason) => reason switch
    {
        PreviewEmptyReason.NothingSelected => "NoDocSelected",
        PreviewEmptyReason.EnvelopedUseDesktop => "PreviewEnvelopedUseDesktop",
        _ => "NoPreview",
    };
}

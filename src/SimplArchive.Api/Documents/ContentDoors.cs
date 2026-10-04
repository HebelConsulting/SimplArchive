namespace SimplArchive.Api.Documents;

/// <summary>
/// Which content doors a version resource advertises (ADR 0865): the enveloped pair, the plaintext pair,
/// both, or neither.
/// </summary>
/// <remarks>
/// <para>
/// A pure decision over three booleans, deliberately separate from the resource builder, so the whole table is
/// asserted exhaustively rather than one tier at a time through a host. (It was extracted when the delivery tiers
/// could not be hosted at all; since ADR 0890 the test module answers the reader-certificate capability, so every
/// enveloping mode can start.) The same recipe as
/// <see cref="StrictEnvelopeDelivery.RefusalFor"/>, whose own note says a cause-to-message table nobody tests
/// is exactly how a collapsed message survives.
/// </para>
/// <para>
/// <b>The invariant worth reading before changing anything here: <c>DeliversEnvelopes</c> never authorises
/// withholding a rel.</b> It says what an ADDITIONAL door offers. Only <c>RefusesPlaintextDoors</c> closes a
/// door, and it is the sole predicate permitted to make a rel disappear. The bug this type exists to end was
/// the delivery question being asked where the door question belongs — for the third time in one file, after
/// <c>RefuseIfStrictAsync</c> and the <c>text-layout</c> rel, because the two are one boolean apart and
/// <c>AppliesAsync</c> is the one that sounds like "does encryption apply here?".
/// </para>
/// </remarks>
/// <param name="Enveloped">
/// Advertise <c>download</c>/<c>preview</c> pointing at the per-reader <c>enveloped-content</c> route.
/// </param>
/// <param name="Plaintext">
/// Advertise the presigned pair. Under <see cref="Enveloped"/> they are the <c>plain-*</c> fallback a client
/// that cannot open a CMS envelope follows (ADR 0830); on their own they ARE <c>download</c>/<c>preview</c>.
/// </param>
public readonly record struct ContentDoors(bool Enveloped, bool Plaintext)
{
    /// <summary>The rel a download is advertised under — <c>plain-download</c> only when both doors serve.</summary>
    public string DownloadRel => Enveloped ? "plain-download" : "download";

    /// <summary>The rel a preview is advertised under — <c>plain-preview</c> only when both doors serve.</summary>
    public string PreviewRel => Enveloped ? "plain-preview" : "preview";

    /// <summary>
    /// The doors this tenant and this reader get.
    /// </summary>
    /// <param name="deliversEnvelopes">This tenant delivers content as an envelope (any delivery tier).</param>
    /// <param name="hasReaderCertificate">
    /// This reader has at least one usable certificate, so an envelope can actually be addressed to them. A
    /// rel that is always present and sometimes fails is the lying affordance ADR 0543 forbids.
    /// </param>
    /// <param name="refusesPlaintextDoors">No door of this tenant's may serve readable bytes.</param>
    public static ContentDoors For(bool deliversEnvelopes, bool hasReaderCertificate, bool refusesPlaintextDoors) =>
        new(Enveloped: deliversEnvelopes && hasReaderCertificate, Plaintext: !refusesPlaintextDoors);
}

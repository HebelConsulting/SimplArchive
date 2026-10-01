namespace SimplArchive.Presentation;

/// <summary>
/// The one check every client applies before sending a certificate to be enrolled (ADR 0864, #1502).
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared because it is at its second occurrence, not speculatively.</b> `saconsole certificates enrol`
/// wrote it first; the desktop dialog is the second and the web dialog will be the third. Three copies of a
/// refusal drift, and the one that drifts is the one nobody re-reads — so the rule lives once and each client
/// renders it in its own words.
/// </para>
/// <para>
/// <b>Only the part a browser can also run lives here.</b> A PEM is text, so this check works everywhere. Its
/// sibling — converting a DER file to PEM — deliberately does not: the Blazor client cannot parse a
/// certificate at all (its render freezes silently, which looks exactly like a binding bug), so DER handling
/// belongs to the clients that have a runtime for it. Putting it here would have produced a leaf that one of
/// its three consumers could not call.
/// </para>
/// </remarks>
public static class EnrolledCertificate
{
    /// <summary>
    /// Why this content must not be sent, or <c>null</c> when it may be.
    /// </summary>
    /// <remarks>
    /// <b>The server refuses key material too, and so does the module's own policy</b> — but the useful moment
    /// to say <i>"that is not what you want to hand over"</i> is while the file is still in front of the
    /// person who chose it. A refusal that arrives from the server has already put the private key on the
    /// wire, which is the one thing this is about.
    /// </remarks>
    public static CertificateRefusal? Refusal(string content) => content switch
    {
        null or "" => CertificateRefusal.Empty,
        _ when content.Contains("PRIVATE KEY", StringComparison.Ordinal) => CertificateRefusal.PrivateKey,
        _ when !content.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal) => CertificateRefusal.NotACertificate,
        _ => null,
    };
}

/// <summary>Why a chosen file cannot be enrolled — one reason per remedy (ADR 0859).</summary>
public enum CertificateRefusal
{
    /// <summary>Nothing was chosen, or the file is empty.</summary>
    Empty,

    /// <summary>
    /// It contains a private key. The remedy is to export the public half instead, and it is the user's.
    /// </summary>
    /// <remarks>
    /// Kept distinct from <see cref="NotACertificate"/> because the two sound alike and lead opposite ways: a
    /// private key means <i>you have the right file and exported too much of it</i>, while the other means
    /// <i>this is not the file</i>. A reader told the wrong one goes hunting for a different file and finds
    /// the same one.
    /// </remarks>
    PrivateKey,

    /// <summary>It is not a PEM certificate. A client that can read DER may still convert it first.</summary>
    NotACertificate,
}

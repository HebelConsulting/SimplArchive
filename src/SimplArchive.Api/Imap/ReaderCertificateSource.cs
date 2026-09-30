namespace SimplArchive.Api.Imap;

/// <summary>
/// Which certificates an IMAP reader is addressed by, and WHERE the answer came from (ADR 0855).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two facts, not one, and the second is the one that is easy to lose.</b>
/// <c>ModuleReaderCertificates.ForAsync</c> returns null when no active module answers the question and an
/// EMPTY LIST when one did and this reader has none — a distinction ADR 0842 makes load-bearing, because
/// where a module answers it is the ONLY source. Collapsing the two into "no certificates" makes an empty
/// answer indistinguishable from silence, and the fetch then tries the encryption service's registry next:
/// the module says "this reader holds none", the registry says "here is one", and a certificate the module
/// has REVOKED goes on opening mail. That is the union ADR 0842 forbids, arrived at by accident.
/// </para>
/// <para>
/// The first draft of this change did exactly that. It is a record rather than two loose fields so the two
/// travel together and a caller cannot read one without the other.
/// </para>
/// </remarks>
/// <param name="Pems">The certificates to envelope to — possibly none.</param>
/// <param name="AnsweredByModule">
/// True when an active module answered. The caller must then NOT consult any other source, whatever
/// <paramref name="Pems"/> holds.
/// </param>
internal sealed record ReaderCertificateSource(IReadOnlyList<string> Pems, bool AnsweredByModule)
{
    /// <summary>Nothing answered, and no other source has been consulted yet.</summary>
    internal static readonly ReaderCertificateSource None = new([], AnsweredByModule: false);

    /// <summary>True when this session envelopes in-process to the certificates above.</summary>
    internal bool Envelopes => Pems.Count > 0;

    /// <summary>
    /// True when the encryption service's registry may still be asked — only when NO module answered.
    /// </summary>
    /// <remarks>
    /// The whole reason this type exists. A module that answered has spoken for this reader, including when
    /// it answered "none": asking further is how a revocation stops revoking.
    /// </remarks>
    internal bool MayConsultRegistry => !AnsweredByModule;

    /// <summary>
    /// The module's answer where there is one, else the self-service column (#1332).
    /// </summary>
    /// <param name="fromModule">
    /// What <c>ModuleReaderCertificates.ForAsync</c> returned — null for silence, empty for "none".
    /// </param>
    /// <param name="columnPem">The user's own registered certificate, or null.</param>
    internal static ReaderCertificateSource Resolve(
        IReadOnlyList<ModuleAbi.ReaderCertificate>? fromModule, string? columnPem) => fromModule switch
        {
            // A module answered. Its answer stands even when empty — see MayConsultRegistry.
            { } answered => new([.. answered.Select(certificate => certificate.CertificatePem)], AnsweredByModule: true),

            // No module enrols certificates on this installation, so the core resolves them as it always
            // has: the self-service column, and then the registry (which the FETCH path asks).
            null => columnPem is { Length: > 0 } own ? new([own], AnsweredByModule: false) : None,
        };
}

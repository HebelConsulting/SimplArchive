namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Which certificates a reader is addressed by, and WHERE the answer came from (ADRs 0855/0856).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two facts, not one, and the second is the one that is easy to lose.</b>
/// <c>ModuleReaderCertificates.ForAsync</c> returns null when no active module answers the question and an
/// EMPTY LIST when one did and this reader has none — a distinction ADR 0842 makes load-bearing, because
/// where a module answers it is the ONLY source. Collapsing the two into "no certificates" made an empty
/// answer indistinguishable from silence, and the fetch then tried the encryption service's registry next:
/// the module said "this reader holds none", the registry said "here is one", and a certificate the module
/// had REVOKED went on opening mail. The registry is retired (ADR 0890), but the column is a second source
/// too, so the distinction still decides whether it may be asked.
/// </para>
/// <para>
/// The first draft of this change did exactly that. It is a record rather than two loose fields so the two
/// travel together and a caller cannot read one without the other.
/// </para>
/// <para>
/// <b>It lives in Infrastructure because TWO callers need the same rule</b> (ADR 0856): the IMAP session and
/// the notification dispatcher. It began in <c>Api.Imap</c> with one caller, and the dispatcher is in
/// Infrastructure — which cannot reference Api — so a second copy was the alternative, and a second copy of a
/// precedence rule is how the two come to disagree about whether a revoked certificate still counts.
/// </para>
/// </remarks>
/// <param name="Pems">The certificates to envelope to — possibly none.</param>
/// <param name="AnsweredByModule">
/// True when an active module answered. No other source was consulted then, whatever <paramref name="Pems"/>
/// holds.
/// </param>
public sealed record ReaderCertificateSource(IReadOnlyList<string> Pems, bool AnsweredByModule)
{
    /// <summary>Nothing answered, and no other source has been consulted yet.</summary>
    public static readonly ReaderCertificateSource None = new([], AnsweredByModule: false);

    /// <summary>True when this session envelopes in-process to the certificates above.</summary>
    public bool Envelopes => Pems.Count > 0;

    /// <summary>
    /// The module's answer where there is one, else the self-service column (#1332).
    /// </summary>
    /// <param name="fromModule">
    /// What <c>ModuleReaderCertificates.ForAsync</c> returned — its OUTCOME says whether a module spoke at
    /// all, which is the distinction this type exists to preserve (ADR 0859).
    /// </param>
    /// <param name="columnPem">The user's own registered certificate, or null.</param>
    public static ReaderCertificateSource Resolve(ReaderCertificateAnswer fromModule, string? columnPem) =>
        fromModule.ModuleSpoke

            // A module spoke. Its answer stands even when it names nothing — and even when the "answer" was
            // a lapsed licence or a failure to ask, because none of those means "this reader has none"
            // (ADR 0859) — and asking the column next is how a revocation would stop revoking.
            ? new([.. fromModule.Certificates.Select(certificate => certificate.CertificatePem)], AnsweredByModule: true)

            // No module answered, so the self-service column is the only source left — the encryption service's
            // registry used to come next and is retired (ADR 0890).
            : columnPem is { Length: > 0 } own ? new([own], AnsweredByModule: false) : None;
}

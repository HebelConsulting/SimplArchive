using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Why the reader-certificate question ended the way it did (#1411, ADR 0859).
/// </summary>
/// <remarks>
/// <para>
/// ADR 0842 requires <b>four refusals to stay distinguishable</b> — the module is not installed, the reader
/// has no certificate enrolled, every certificate was filtered out, and the licence has lapsed — because
/// they have different fixes and <i>"a single 'no certificate available' collapses all four into the one an
/// administrator cannot act on"</i>.
/// </para>
/// <para>
/// <b>The seam collapsed them anyway.</b> It answered null for "not installed" and an empty list for
/// everything else: no certificate, all filtered out, licence lapsed, <b>and the module throwing</b>. So a
/// reader whose installation was broken was told to <i>"register a current certificate and try again"</i> —
/// advice that is wrong, and unactionable, because the fault is not theirs.
/// </para>
/// <para>
/// <b>Three of the four are answerable here; one is not.</b> Not-installed, licence-lapsed and could-not-ask
/// are facts the core observes. <i>No certificate enrolled</i> versus <i>every certificate filtered out</i>
/// is known only to the module — both arrive as an empty answer — so telling those apart needs the ABI to
/// carry a reason, which is not this change. <see cref="Answered"/> therefore means "the module spoke", and
/// an empty set under it is still one refusal rather than two.
/// </para>
/// </remarks>
public enum ReaderCertificateOutcome
{
    /// <summary>No loaded module declares the capability — the core resolves certificates as it did before.</summary>
    NoModule,

    /// <summary>A module answered. Its answer stands alone, including when it names no certificate.</summary>
    Answered,

    /// <summary>A module declares the capability but is not active for this tenant: the licence has lapsed or was never filed.</summary>
    LicenceLapsed,

    /// <summary>A module was asked and threw. Nothing was learned — which is not the same as "none".</summary>
    AskFailed,
}

/// <summary>What the module said, and whether it was able to say it.</summary>
/// <param name="Outcome">Why this ended as it did — see <see cref="ReaderCertificateOutcome"/>.</param>
/// <param name="Certificates">
/// The certificates to address. Non-empty only when <paramref name="Outcome"/> is
/// <see cref="ReaderCertificateOutcome.Answered"/>.
/// </param>
/// <remarks>
/// A record rather than a tuple so the outcome cannot be dropped on the way to a caller. Two of the three
/// callers — the IMAP funnel and the notification dispatcher — deliberately IGNORE the outcome and fail open
/// to plaintext, which is their contract; it is the strict content read that must refuse differently per
/// cause, and it is the one whose message was wrong.
/// </remarks>
public sealed record ReaderCertificateAnswer(
    ReaderCertificateOutcome Outcome,
    IReadOnlyList<ReaderCertificate> Certificates)
{
    /// <summary>Nothing on this installation enrols certificates.</summary>
    public static readonly ReaderCertificateAnswer NoModule = new(ReaderCertificateOutcome.NoModule, []);

    /// <summary>A module answered with this set — possibly empty.</summary>
    public static ReaderCertificateAnswer Answered(IReadOnlyList<ReaderCertificate> certificates) =>
        new(ReaderCertificateOutcome.Answered, certificates);

    /// <summary>Declared, but not licensed for this tenant.</summary>
    public static readonly ReaderCertificateAnswer LicenceLapsed = new(ReaderCertificateOutcome.LicenceLapsed, []);

    /// <summary>Asked, and it threw.</summary>
    public static readonly ReaderCertificateAnswer AskFailed = new(ReaderCertificateOutcome.AskFailed, []);

    /// <summary>
    /// True when a module spoke for this reader, whatever it said — so no other source may be consulted.
    /// </summary>
    /// <remarks>
    /// Every outcome but <see cref="ReaderCertificateOutcome.NoModule"/> closes the core's own sources. A
    /// lapsed licence or a broken module has not said "this reader has none"; falling through to the column
    /// or the registry on either would let a certificate the module revoked go on opening content — the union
    /// ADR 0842 forbids.
    /// </remarks>
    public bool ModuleSpoke => Outcome is not ReaderCertificateOutcome.NoModule;
}

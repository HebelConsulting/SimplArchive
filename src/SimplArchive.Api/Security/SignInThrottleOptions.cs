namespace SimplArchive.Api.Security;

/// <summary>
/// Tuning for <see cref="SignInThrottle"/> (ADR 0716), bound from the <c>SignInThrottle</c> configuration
/// section. There is no off switch: a security control with a documented bypass is a control an installation
/// eventually runs without, and the production-readiness posture (ADR 0343) is that dev-grade settings are
/// refused rather than offered.
/// </summary>
public sealed class SignInThrottleOptions
{
    public const string SectionName = "SignInThrottle";

    /// <summary>
    /// Failed attempts allowed against one identity, on one surface, before the first block. Five is the
    /// NIST 800-63B spirit — enough that a person who mistypes twice and then reaches for their password
    /// manager never notices, few enough that guessing is hopeless.
    /// </summary>
    public int IdentityFreeAttempts { get; set; } = 5;

    /// <summary>
    /// DISTINCT identities that may fail from one client address before it is blocked. Higher than the
    /// per-identity allowance on purpose: this counter's job is spraying, and the address is frequently a
    /// NAT gateway or a reverse proxy shared by every legitimate user of the installation.
    /// </summary>
    public int AddressFreeIdentities { get; set; } = 25;

    /// <summary>How long a counter survives without a further failure. Longer than the longest block.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The block ladder: each further run of failures escalates one rung, and the last rung repeats forever.
    /// A DECAYING block rather than an administrator-reset lockout, because a lockout an attacker can trigger
    /// against any address they know the email of is a denial-of-service tool handed out with the login page.
    /// </summary>
    public IList<TimeSpan> Penalties { get; set; } =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
    ];
}

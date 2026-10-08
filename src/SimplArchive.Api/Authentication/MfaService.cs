using System.Security.Cryptography;
using System.Text;
using OtpNet;
using SimplArchive.Api.Security;

namespace SimplArchive.Api.Authentication;

// Two-factor (TOTP) helpers (ADR "MFA (interactive login, TOTP)") — secret generation, the otpauth URI + its
// QR, code verification with a small time skew, and one-time recovery codes. Stateless, registered as a
// singleton. Lives in the Api project (near the login page + the users controller that use it), matching where
// ProfilePhotoValidator / TenantProvisioningService already sit.
public sealed class MfaService(IThrottleCounterStore replayStore, ILogger<MfaService> logger)
{
    private const string Issuer = "SimplArchive";
    private const int RecoveryCodeCount = 10;
    // Base32-ish alphabet without ambiguous characters (no 0/1/O/I/L).
    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    // A base32 shared secret for a new enrollment.
    public string GenerateSecret() => Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));

    // otpauth://totp/<Issuer>:<account>?secret=...&issuer=<Issuer> — what an authenticator app consumes.
    public string BuildOtpauthUri(string secret, string account)
    {
        var label = Uri.EscapeDataString($"{Issuer}:{account}");
        return $"otpauth://totp/{label}?secret={secret}&issuer={Uri.EscapeDataString(Issuer)}&digits=6&period=30";
    }

    // The enrollment QR as a PNG data URL (what the client renders inline).
    public string GenerateQrDataUrl(string otpauthUri) => QrCodes.PngDataUrl(otpauthUri);

    /// <summary>
    /// Verifies a 6-digit TOTP against the secret and BURNS it, so the same code cannot be used twice
    /// (#847, A02).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One 30s step of skew either way is 90 seconds in which a code stays valid</b>, and without this it
    /// stayed valid for ALL of it, however many times it was presented. A code read over somebody's shoulder,
    /// out of a screen share, or from a phishing page relayed in real time could be replayed for the rest of
    /// its window — which is the whole of the attack TOTP is there to stop. The code is one-time in the
    /// standard's name and was not one-time here.
    /// </para>
    /// <para>
    /// <b>Burning the matched STEP, not the code.</b> The library reports which timestep matched, so the claim
    /// is per (user, secret, step): burning the digits would be wrong twice over — two users can legitimately
    /// hold the same six digits at the same instant, and the same user's next code is a different step and must
    /// still work.
    /// </para>
    /// <para>
    /// <b>The SECRET is in the key, and leaving it out was a real bug the suite caught.</b> Keyed on
    /// (user, step) alone, a user could spend only one code per 30-second step for ANY purpose — so
    /// re-enrolling with a fresh secret seconds after using the old one was refused as a replay, which it is
    /// not: a different secret's code shares nothing with the old one but the clock. Fingerprinted rather than
    /// included, for the same reason the throttle fingerprints its identities — a counter store should not hold
    /// anything worth stealing.
    /// </para>
    /// <para>
    /// <b>Takes the user id, so no caller can verify without burning.</b> The three call sites all have one;
    /// making it a parameter rather than an optional extra is what stops a fourth site reintroducing the
    /// replay by forgetting a step.
    /// </para>
    /// <para>
    /// <b>FAILS OPEN</b>, as the throttle does for the same store and the same reason (ADR 0716): if the
    /// counter store is unreachable the code is accepted and the outage is logged, because failing closed
    /// would turn a cache hiccup into "nobody with MFA can sign in" — a self-inflicted outage in exchange for
    /// a defence that is additive. The credential check itself is unaffected either way.
    /// </para>
    /// </remarks>
    public async Task<bool> VerifyTotpAsync(
        Guid userId, string secret, string code, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        if (!totp.VerifyTotp(code.Trim(), out var step, new VerificationWindow(previous: 1, future: 1)))
        {
            return false;
        }

        try
        {
            // CountAsync increments and returns the new value, so the FIRST use of a step comes back 1 and
            // every later one comes back higher. The window outlives the verification window, so a step cannot
            // come back up for reuse while it is still being accepted.
            var uses = await replayStore.CountAsync(
                $"totp:{userId:N}:{SecretFingerprint(secret)}:{step}", TimeSpan.FromMinutes(2), cancellationToken);
            if (uses > 1)
            {
                // Warning, not Debug: a correct code arriving twice is not something a person's authenticator
                // does. Either somebody is replaying it or a client is double-submitting, and an administrator
                // should be able to tell those apart from the log rather than from a support call.
                logger.LogWarning(
                    "A TOTP code for user {UserId} was presented again for a timestep already used — refused. "
                    + "A legitimate authenticator does not resend a code; this is a replay or a client "
                    + "double-submitting.", userId);

                return false;
            }
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "The TOTP replay store is unavailable, so a code could be reused within its {Window}s window "
                + "until it recovers. The code's own verification is unaffected.", 90);
        }

        return true;
    }

    // Short, non-reversible, and enough to separate two secrets. The counter store is not a place to keep a
    // TOTP secret, and the key only has to distinguish — it never has to be read back.
    private static string SecretFingerprint(string secret) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))[..16];

    // A fresh set of one-time recovery codes as (plaintext shown once, hash stored).
    public IReadOnlyList<(string Plaintext, string Hash)> GenerateRecoveryCodes()
    {
        var codes = new List<(string, string)>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var plaintext = $"{RandomChunk(5)}-{RandomChunk(5)}";
            codes.Add((plaintext, HashRecoveryCode(plaintext)));
        }

        return codes;
    }

    // SHA-256 hex of the normalized code — recovery codes are high-entropy random values, so an unsalted fast
    // hash is the standard choice (unlike passwords) and lets the login path match by direct lookup.
    public string HashRecoveryCode(string code)
    {
        var normalized = NormalizeRecoveryCode(code);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    // Strips spacing/dashes and lowercases so display formatting doesn't affect matching.
    public static string NormalizeRecoveryCode(string code) =>
        new(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string RandomChunk(int length)
    {
        var chars = new char[length];
        for (var i = 0; i < length; i++)
        {
            chars[i] = RecoveryAlphabet[RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)];
        }

        return new string(chars);
    }
}

using System.Security.Cryptography;

namespace SimplArchive.Api.Documents;

// Generates the opaque credential that appears in an external link's URL (ADR 0546).
//
// 256 bits from a cryptographic RNG, rendered base64url so it is safe in a path segment without escaping.
//
// Deliberately NOT a Guid. A v4 Guid's ~122 bits would be enough today, but the TYPE is the hazard: it invites a
// later "optimisation" to Guid.CreateVersion7(), which embeds a timestamp and is therefore partly predictable
// from when the link was created. Since this token is the ONLY thing standing between a URL and a document, the
// safe property is worth making structural rather than a comment somebody may not read.
public static class ExternalLinkToken
{
    // 32 bytes → 43 base64url characters, no padding.
    private const int TokenBytes = 32;

    public static string Create() => Base64UrlEncode(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>
    /// A token derived deterministically from <paramref name="seed"/> — <b>DEMO SEED DATA ONLY</b> (issue #405).
    /// </summary>
    /// <remarks>
    /// This is the opposite of what <see cref="Create"/> guarantees: the output is a pure function of its input,
    /// so anyone who knows the seed string knows the token. It exists because the kiosk re-seeds nightly from
    /// empty volumes, and a fresh random token every night would break every URL that had been shared, bookmarked
    /// or written into a demo script. The demo tenant is a public showcase with published credentials, so a
    /// predictable link into it gives away nothing that is not already open.
    ///
    /// Never call this for a real link. SHA-256 keeps the 43-character base64url shape of a genuine token, which
    /// is the point — the demo shows the real URL format — and is also why a caller cannot tell the two apart by
    /// looking, so the restriction has to live here in the name and this comment.
    /// </remarks>
    public static string DeriveForDemoSeed(string seed) =>
        Base64UrlEncode(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    // A token is compared against the database, so the lookup itself is the comparison — but any in-process
    // comparison of a candidate against a known value must be time-independent, or the difference in how long a
    // mismatch takes leaks where it diverged.
    public static bool Equals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

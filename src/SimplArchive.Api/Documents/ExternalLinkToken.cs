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

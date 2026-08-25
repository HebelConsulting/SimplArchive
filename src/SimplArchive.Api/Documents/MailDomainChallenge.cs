using System.Security.Cryptography;

namespace SimplArchive.Api.Documents;

/// <summary>The DNS challenge a mail-domain claim is proven with (#667).</summary>
public static class MailDomainChallenge
{
    /// <summary>The prefix the published record carries, so a reader can tell what the record is for.</summary>
    /// <remarks>
    /// A zone accumulates verification records from every service an organisation uses, and one that is only
    /// an opaque string is one nobody dares delete years later. Naming ourselves in it is the difference
    /// between a record that can be cleaned up and a record that is kept forever out of caution.
    /// </remarks>
    public const string Prefix = "simplarchive-domain-verification=";

    /// <summary>A fresh challenge value: the prefix plus 160 bits of randomness, URL-safe.</summary>
    /// <remarks>
    /// <b>Cryptographically random, not a GUID.</b> The token is the whole proof — anyone who can guess one
    /// can claim a domain they do not own — and a GUID is neither specified to be unpredictable nor generated
    /// from a CSPRNG on every platform. 160 bits is well past what a guessing attack could reach against a
    /// value that also has to be published in the victim's own zone.
    /// </remarks>
    public static string NewToken() =>
        Prefix + Base64Url(RandomNumberGenerator.GetBytes(20));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Whether a string is shaped like a mail domain — asked before anything is stored (#667).</summary>
/// <remarks>
/// Deliberately a SHAPE check, not a reachability one: whether the domain exists and whether this tenant owns
/// it are what the DNS challenge answers, and refusing a syntactically fine name because a resolver was slow
/// would be a worse error than the one it prevents. What this stops is the mistake a person makes at the
/// keyboard — an email address instead of a domain, a URL, a stray space.
/// </remarks>
public static class MailDomainName
{
    public static bool IsWellFormed(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253)
        {
            return false;
        }

        // No scheme, no path, no user part, no spaces — the four ways a person types something that is not a
        // domain into a box asking for one.
        if (domain.Contains('@') || domain.Contains('/') || domain.Contains(':') || domain.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var labels = domain.Split('.');

        // At least two labels: a single label is a host on some search domain, never a mail domain in its own
        // right, and accepting one would claim something like "localhost" tenant-wide.
        return labels.Length >= 2 && labels.All(IsLabel);
    }

    private static bool IsLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SimplArchive.ApiClient;

/// <summary>
/// What the HOST supplies to the shared API client (ADR 0917): which installation, as which client, how to open an
/// envelope, where sessions persist, and where to log. The desktop and the mobile app each set these once.
/// </summary>
/// <remarks>
/// Static, like <see cref="ApiCore.Authenticated"/> beside it, because the content funnel is static: every read path
/// (preview, open, drag-out, thumbnails) reaches it without an <see cref="ApiCore"/> in hand, and the desktop's tests
/// retarget it by setting the address alone. The defaults keep a host that forgot something honest:
/// - no session persistence (in memory);
/// - no log (a null logger);
/// - an envelope that cannot be opened says so, rather than handing ciphertext on as content.
/// </remarks>
public static class ApiClientSettings
{
    /// <summary>The installation's address (the API root's origin). Settable so a host or a test can retarget it.</summary>
    public static string ApiRootUrl { get; set; } = "http://localhost:8080";

    /// <summary>The OAuth client id this host signs in as, which renewal and revocation present.</summary>
    public static string ClientId { get; set; } = string.Empty;

    /// <summary>Opens a CMS envelope the server served (ADR 0830). The desktop's uses its card or certificate
    /// store; the phone's its PIV key.</summary>
    public static IEnvelopeOpener EnvelopeOpener { get; set; } = NoEnvelopeOpener.Instance;

    /// <summary>Where a session's tokens persist between runs, keyed by installation.</summary>
    public static ITokenSessionStore Sessions { get; set; } = new InMemoryTokenSessionStore();

    public static ILogger Logger { get; set; } = NullLogger.Instance;
}

/// <summary>Opens a CMS envelope (<c>application/pkcs7-mime</c>) the server served to this reader.</summary>
public interface IEnvelopeOpener
{
    /// <summary>The plaintext and its content type, or an exception that names what is missing (a key, a card).</summary>
    Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType);
}

/// <summary>Whether a served content type is a CMS envelope. Defined once, because the funnel and a host's
/// card-removal path must agree about it.</summary>
public static class Envelopes
{
    public static bool IsEnvelope(string contentType) =>
        contentType.Contains("pkcs7-mime", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A host that configured no opener: an envelope is refused by name, never passed on as content.</summary>
internal sealed class NoEnvelopeOpener : IEnvelopeOpener
{
    public static readonly NoEnvelopeOpener Instance = new();

    public Task<(byte[] Bytes, string ContentType)> OpenAsync(byte[] envelope, string contentType) =>
        throw new InvalidOperationException("This client was served an encrypted envelope and has no way to open it configured.");
}

/// <summary>Where a host persists sessions; the desktop's is the OS secret store.</summary>
public interface ITokenSessionStore
{
    TokenSession? For(string apiRootUrl);

    void Set(string apiRootUrl, TokenSession session);

    void Clear(string apiRootUrl);
}

/// <summary>Sessions for this run only.</summary>
public sealed class InMemoryTokenSessionStore : ITokenSessionStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TokenSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public TokenSession? For(string apiRootUrl) => _sessions.TryGetValue(apiRootUrl, out var s) ? s : null;

    public void Set(string apiRootUrl, TokenSession session) => _sessions[apiRootUrl] = session;

    public void Clear(string apiRootUrl) => _sessions.TryRemove(apiRootUrl, out _);
}

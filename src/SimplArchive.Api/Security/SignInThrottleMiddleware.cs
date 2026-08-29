using System.Text;

namespace SimplArchive.Api.Security;

/// <summary>
/// Applies <see cref="ISignInThrottle"/> to the two HTTP doors whose outcome is legible from the response
/// (ADR 0716): the OAuth token endpoint, and HTTP Basic — which is WebDAV, CalDAV and CardDAV, all three
/// verifying the same app-specific DAV password.
/// </summary>
/// <remarks>
/// <para>
/// Middleware rather than a change at each door, for one reason each. The token endpoint's wrong-secret
/// refusal is issued by OpenIddict's own client authentication BEFORE the controller action runs, so there is
/// no line in <c>TokenController</c> where a wrong secret is known. And Basic auth is verified in two
/// unrelated places — <c>WebDavMiddleware</c> and <c>DavBasicAuthenticationHandler</c> — which a single
/// header-shaped rule covers without either of them learning about throttling.
/// </para>
/// <para>
/// The interactive login page is NOT here: a failed sign-in there answers <c>200</c> with an error rendered
/// on the page, so the response carries no signal to key on. It calls the throttle itself.
/// </para>
/// </remarks>
public sealed class SignInThrottleMiddleware
{
    private const string TokenPath = "/connect/token";
    private const string BasicPrefix = "Basic ";

    private readonly RequestDelegate _next;
    private readonly ILogger<SignInThrottleMiddleware> _logger;

    public SignInThrottleMiddleware(RequestDelegate next, ILogger<SignInThrottleMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ISignInThrottle throttle)
    {
        var attempt = await IdentifyAsync(context);
        if (attempt is null)
        {
            await _next(context);

            return;
        }

        var (surface, identity) = attempt.Value;
        var address = context.Connection.RemoteIpAddress?.ToString();

        var verdict = await throttle.CheckAsync(surface, identity, address, context.RequestAborted);
        if (!verdict.Allowed)
        {
            // Debug, not Warning: the block itself was already logged at Warning by the throttle, and every
            // request an attacker sends afterwards lands here. One alert per block, not one per packet.
            _logger.LogDebug(
                "Refused a {Surface} credential attempt: throttled for another {RetryAfter}", surface, verdict.RetryAfter);

            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(verdict.RetryAfter.TotalSeconds)).ToString();

            return;
        }

        await _next(context);

        // 401 is the credential refusal and nothing else: OpenIddict answers it for an unknown client or a
        // wrong secret, and both DAV doors answer it for a bad password. The endpoint's OTHER refusals — an
        // unsupported grant type, a deactivated account, a replayed authorization code — are 400s, and
        // counting those would let a misconfigured integration, or the web client's own code exchange, throttle
        // the door for every legitimate caller sharing that client id.
        if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            await throttle.RecordFailureAsync(surface, identity, address, context.RequestAborted);
        }
        else if (context.Response.StatusCode < StatusCodes.Status400BadRequest)
        {
            await throttle.RecordSuccessAsync(surface, identity, address, context.RequestAborted);
        }
    }

    /// <summary>
    /// What credential, if any, this request presents. Returns <c>null</c> when there is nothing to throttle —
    /// including the very common case of a DAV client's first, deliberately unauthenticated request, which
    /// earns its 401 by design and must never count as a failed attempt.
    /// </summary>
    private static async Task<(SignInSurface Surface, string Identity)?> IdentifyAsync(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var basic = header.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase)
            ? UserNameFrom(header)
            : null;

        if (context.Request.Path.StartsWithSegments(TokenPath, StringComparison.OrdinalIgnoreCase))
        {
            // client_secret_basic puts the client id in the Basic user name; client_secret_post puts it in the
            // form, which is what this installation's own clients send.
            if (basic is not null)
            {
                return (SignInSurface.Token, basic);
            }

            if (!context.Request.HasFormContentType)
            {
                return null;
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted);
            var clientId = form["client_id"].ToString();

            return string.IsNullOrWhiteSpace(clientId) ? null : (SignInSurface.Token, clientId);
        }

        return basic is null ? null : (SignInSurface.Dav, basic.ToUpperInvariant());
    }

    private static string? UserNameFrom(string header)
    {
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[BasicPrefix.Length..].Trim()));
        }
        catch (FormatException)
        {
            return null;
        }

        var separator = decoded.IndexOf(':');

        return separator > 0 ? decoded[..separator] : null;
    }
}

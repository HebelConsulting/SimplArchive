using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SimplArchive.ApiClient;

/// <summary>What a completed sign-in produced.</summary>
/// <param name="RefreshToken">Null when the server issued none: an older deployment, or offline_access refused.</param>
/// <param name="ExpiresAt">When the access token stops working, as an instant rather than a duration.</param>
/// <remarks>The renewal fields default to "none, already expired", the honest reading of a server that issued no
/// refresh token.</remarks>
public sealed record AuthResult(string AccessToken, string? Email, string? RefreshToken = null, DateTimeOffset ExpiresAt = default);

/// <summary>
/// OAuth 2.0 Authorization Code + PKCE for a native app (RFC 8252), the part every host shares (ADR 0917):
/// discovery, the verifier and challenge, the state, the authorize address, and the code exchange. A host only
/// opens <see cref="AuthorizeUrl"/> and hands back the redirect's query: the desktop through a loopback listener,
/// the phone through its web-authentication tab (ADR 0916).
/// </summary>
public sealed class PkceSignIn
{
    private static readonly HttpClient Http = new();

    private readonly string _tokenEndpoint;
    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly string _verifier;
    private readonly string _state;

    private PkceSignIn(string authorizeUrl, string tokenEndpoint, string clientId, string redirectUri, string verifier, string state) =>
        (AuthorizeUrl, _tokenEndpoint, _clientId, _redirectUri, _verifier, _state) = (authorizeUrl, tokenEndpoint, clientId, redirectUri, verifier, state);

    /// <summary>The address to open in the system's browser. Carries the challenge and the state: never log it.</summary>
    public string AuthorizeUrl { get; }

    /// <summary>Discovers the endpoints and prepares one sign-in.</summary>
    /// <param name="forceLogin">Adds <c>prompt=login</c>, so a browser still holding a session re-authenticates (ADR 0334).</param>
    /// <param name="loginHint">An email the login page pre-fills (ADR 0466).</param>
    public static async Task<PkceSignIn> StartAsync(
        string apiRootUrl, string clientId, string redirectUri, string scopes,
        bool forceLogin = false, string? loginHint = null, CancellationToken cancellationToken = default)
    {
        // Logged because a sign-in that fails here fails BEFORE the browser opens, which reads as "nothing happened"
        // (ADR 0613). The address is safe to record; nothing below it is.
        ApiClientSettings.Logger.LogDebug("Discovering the identity endpoints at {ApiRootUrl}", apiRootUrl);
        var discovery = await Http.GetFromJsonAsync<JsonElement>($"{apiRootUrl.TrimEnd('/')}/.well-known/openid-configuration", cancellationToken);
        var authorizationEndpoint = discovery.GetProperty("authorization_endpoint").GetString()!;
        var tokenEndpoint = discovery.GetProperty("token_endpoint").GetString()!;

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var authorizeUrl =
            $"{authorizationEndpoint}?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&response_type=code&scope={Uri.EscapeDataString(scopes)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state={state}" +
            (forceLogin ? "&prompt=login" : string.Empty) +
            (string.IsNullOrWhiteSpace(loginHint) ? string.Empty : $"&login_hint={Uri.EscapeDataString(loginHint)}");
        return new PkceSignIn(authorizeUrl, tokenEndpoint, clientId, redirectUri, verifier, state);
    }

    /// <summary>
    /// Exchanges the code the redirect carried for tokens, or null when the redirect is not this sign-in's (no code,
    /// or another state: a stale tab, or a forged callback).
    /// </summary>
    public async Task<AuthResult?> CompleteAsync(string? code, string? returnedState, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(code) || !string.Equals(returnedState, _state, StringComparison.Ordinal))
        {
            ApiClientSettings.Logger.LogWarning("A sign-in redirect arrived without a code or with another state; it was ignored");
            return null;
        }

        using var response = await Http.PostAsync(_tokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _redirectUri,
            ["client_id"] = _clientId,
            ["code_verifier"] = _verifier,
        }), cancellationToken);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken), DateTimeOffset.UtcNow);
    }

    /// <summary>A token response as the result. <c>expires_in</c> is seconds from NOW, so it becomes an instant at
    /// once; a server that sends none is treated as already expired, so the first request renews.</summary>
    public static AuthResult Parse(JsonElement tokens, DateTimeOffset now)
    {
        var lifetime = tokens.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.Zero;
        return new AuthResult(
            tokens.GetProperty("access_token").GetString()!,
            tokens.TryGetProperty("id_token", out var idToken) ? ReadEmailFromJwt(idToken.GetString()) : null,
            tokens.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            now + lifetime);
    }

    /// <summary>The email claim of an id token, read without validating it: the token came straight from the token
    /// endpoint over TLS, and the email is only shown and remembered, never trusted for access.</summary>
    public static string? ReadEmailFromJwt(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: >= 2 })
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(Base64UrlDecode(parts[1]));
            return payload.TryGetProperty("email", out var email) ? email.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return null;
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

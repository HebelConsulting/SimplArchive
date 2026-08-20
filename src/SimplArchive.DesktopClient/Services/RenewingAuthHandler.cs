using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Attaches the bearer token to every request, and renews it before — or, failing that, after — it expires.
/// </summary>
/// <remarks>
/// <para>
/// The client used to capture one access token at login and send it for ever. An hour later every request came
/// back <c>401 (unauthorized)</c> with nothing renewing anything, which reads to the user as the app having
/// gone stale rather than as a session having ended.
/// </para>
/// <para>
/// <b>Both halves are needed.</b> Renewing AHEAD of expiry is what keeps a working session working. Renewing
/// ON a 401 is what recovers the cases the clock cannot predict — a server restarted, a token revoked, a
/// machine that slept through its own expiry. Neither alone is enough: the first assumes the clock is the only
/// reason a token stops working, the second turns every expiry into a user-visible failure first.
/// </para>
/// <para>
/// Renewal is serialised. Twenty parallel requests noticing an expired token at once would otherwise fire
/// twenty refreshes, and with rotation ON (each refresh invalidates the one used) nineteen of them would be
/// presenting a token that the first had already spent — indistinguishable, from the server, from theft.
/// </para>
/// </remarks>
public sealed class RenewingAuthHandler : DelegatingHandler
{
    private readonly string _apiRootUrl;
    private readonly TokenSession.Holder _session;
    private readonly SemaphoreSlim _renewGate = new(1, 1);

    public RenewingAuthHandler(string apiRootUrl, TokenSession.Holder session, HttpMessageHandler inner)
        : base(inner)
    {
        _apiRootUrl = apiRootUrl;
        _session = session;
    }

    /// <summary>
    /// Raised when a session cannot be renewed and the user has to sign in again — with the server named.
    /// </summary>
    /// <remarks>
    /// The profile matters: somebody moving between production, integration and a local stack needs to be told
    /// WHICH one dropped them, and a dialog that only says "your session expired" makes them guess.
    /// </remarks>
    public static event Action<string>? SessionEnded;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var session = _session.Value;

        if (session is { CanRenew: true, NeedsRenewal: true })
        {
            session = await RenewAsync(session, cancellationToken);
        }

        Attach(request, session);

        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // A 401 the clock did not predict. Renew ONCE and replay — not in a loop, because a token the server
        // keeps refusing is a session that has ended, and retrying it is just a slower way to say so.
        var current = _session.Value;
        if (current is not { CanRenew: true })
        {
            _session.Value = null;
            TokenSessions.Current.Clear(_apiRootUrl);
            SessionEnded?.Invoke(_apiRootUrl);
            return response;
        }

        var renewed = await RenewAsync(current, cancellationToken);
        if (renewed is null || string.IsNullOrEmpty(renewed.AccessToken))
        {
            return response;
        }

        response.Dispose();

        var replay = await CloneAsync(request, cancellationToken);
        Attach(replay, renewed);
        return await base.SendAsync(replay, cancellationToken);
    }

    private static void Attach(HttpRequestMessage request, TokenSession? session)
    {
        if (session is { AccessToken.Length: > 0 })
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        }
    }

    /// <summary>Exchanges the refresh token for a new pair, or ends the session when the server refuses.</summary>
    private async Task<TokenSession?> RenewAsync(TokenSession session, CancellationToken cancellationToken)
    {
        await _renewGate.WaitAsync(cancellationToken);
        try
        {
            // Another request may have renewed while this one waited at the gate — the common case under load,
            // and the whole point of serialising. Take theirs rather than spending a second refresh token.
            var latest = _session.Value;
            if (latest is { NeedsRenewal: false, AccessToken.Length: > 0 })
            {
                return latest;
            }

            var refreshToken = latest?.RefreshToken ?? session.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken))
            {
                return latest ?? session;
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiRootUrl.TrimEnd('/')}/connect/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = DesktopClientOptions.ClientId,
                }),
            };

            // Sent through the INNER handler: this request carries its own credential and must not be given a
            // bearer, retried on 401, or recursed back into this method.
            using var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // The server has refused this refresh token — expired, revoked, rotated away, or the account
                // deactivated. It will not start working, so the session is over and the stored token goes with
                // it (see TokenSessions.Clear: a token kept here would fail every future launch first).
                _session.Value = null;
                TokenSessions.Current.Clear(_apiRootUrl);
                SessionEnded?.Invoke(_apiRootUrl);
                return null;
            }

            var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var accessToken = tokens.TryGetProperty("access_token", out var access) ? access.GetString() : null;
            if (string.IsNullOrEmpty(accessToken))
            {
                _session.Value = null;
                TokenSessions.Current.Clear(_apiRootUrl);
                SessionEnded?.Invoke(_apiRootUrl);
                return null;
            }

            // Rotation is on server-side, so a NEW refresh token comes back and the old one is now spent. Keeping
            // the old one would guarantee the next renewal fails.
            var rotated = tokens.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : refreshToken;
            var lifetime = tokens.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.Zero;

            var next = new TokenSession(accessToken, rotated, DateTimeOffset.UtcNow + lifetime);
            _session.Value = next;

            // The rotated refresh token is also persisted for the SERVER, so the next launch starts signed in.
            TokenSessions.Current.Set(_apiRootUrl, next);
            return next;
        }
        catch (HttpRequestException)
        {
            // The server is unreachable — a different thing from a refused token, and NOT a reason to throw the
            // stored refresh token away. The connectivity path already owns this case; the session survives so
            // that coming back online resumes rather than re-authenticates.
            return null;
        }
        finally
        {
            _renewGate.Release();
        }
    }

    /// <summary>A replayable copy: a sent request cannot be sent again, and its content stream is consumed.</summary>
    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            var buffered = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(buffered);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }
}

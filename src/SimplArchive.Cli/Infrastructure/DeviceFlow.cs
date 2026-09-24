using System.Net.Http.Json;
using System.Text.Json;

namespace SimplArchive.Cli.Infrastructure;

/// <summary>
/// The client half of the device authorization grant (RFC 8628, ADR 0823): ask for a code, tell the
/// administrator where to approve it, and poll until they have.
/// </summary>
/// <remarks>
/// This exists because an administrative CLI is typically run over SSH on a host with no browser and no
/// desktop session, where the desktop client's loopback redirect cannot work at all — it needs a browser on
/// the machine running the command. Here the tool prints a code and the approval happens in whatever browser
/// the administrator already has, on whatever machine they are sitting at.
/// </remarks>
public sealed class DeviceFlow(HttpClient http)
{
    /// <summary>The client id the installation registers for this tool (<c>SaConsoleClientSeeder</c>).</summary>
    public const string ClientId = "saconsole";

    /// <summary>What the device endpoint answered: what to show a person, and what to poll with.</summary>
    public sealed record Authorization(
        string DeviceCode,
        string UserCode,
        string VerificationUri,
        string? VerificationUriComplete,
        TimeSpan Interval,
        DateTimeOffset ExpiresAt);

    public async Task<Authorization> RequestAsync(CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,

            // "openid" and nothing else. offline_access is deliberately NOT asked for: the installation does
            // not grant this client a refresh token, and a session that cannot outlive its access token is
            // the decision rather than a limitation — nothing long-lived is left on a host the approver may
            // not be sitting at.
            ["scope"] = "openid",
        });

        using var response = await http.PostAsync("connect/device", form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new CliException(DescribeTokenError(body,
                $"The installation refused a device authorization request ({(int)response.StatusCode}). "
                + "It may be older than the device grant, or not a SimplArchive Api."));
        }

        var json = JsonDocument.Parse(body).RootElement;

        string Required(string name) => json.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s
            ? s
            : throw new CliException($"The device endpoint answered without '{name}'.");

        // RFC 8628 §3.2 makes `interval` OPTIONAL and says 5 seconds when it is absent. Defaulting to zero
        // instead would hammer the endpoint into the slow_down it is trying to avoid.
        var interval = json.TryGetProperty("interval", out var i) && i.TryGetInt32(out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(5);

        var lifetime = json.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secondsLeft)
            ? TimeSpan.FromSeconds(secondsLeft)
            : TimeSpan.FromMinutes(10);

        return new Authorization(
            Required("device_code"),
            Required("user_code"),
            Required("verification_uri"),
            json.TryGetProperty("verification_uri_complete", out var c) ? c.GetString() : null,
            interval,
            DateTimeOffset.UtcNow + lifetime);
    }

    /// <summary>
    /// Polls until the administrator answers. Returns the access token, or throws with what they decided.
    /// </summary>
    /// <remarks>
    /// The three answers are told apart deliberately. <c>authorization_pending</c> is the normal case and
    /// means keep waiting; <c>slow_down</c> is the server asking for a longer gap and RFC 8628 §3.5 requires
    /// the interval to grow by five seconds each time — a client that ignores it is one the server is
    /// entitled to refuse; <c>access_denied</c> is a PERSON saying no, and retrying that would be pestering
    /// somebody who already answered.
    /// </remarks>
    public async Task<string> PollAsync(
        Authorization authorization, Action<TimeSpan> onWait, CancellationToken cancellationToken)
    {
        var interval = authorization.Interval;

        while (true)
        {
            if (DateTimeOffset.UtcNow >= authorization.ExpiresAt)
            {
                throw new CliException(
                    "The code expired before it was approved. Run the command again to get a new one.");
            }

            onWait(interval);
            await Task.Delay(interval, cancellationToken);

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["device_code"] = authorization.DeviceCode,
                ["client_id"] = ClientId,
            });

            using var response = await http.PostAsync("connect/token", form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var json = JsonDocument.Parse(body).RootElement;

            if (response.IsSuccessStatusCode)
            {
                return json.TryGetProperty("access_token", out var token) && token.GetString() is { Length: > 0 } value
                    ? value
                    : throw new CliException("The token endpoint answered success without an access_token.");
            }

            switch (json.TryGetProperty("error", out var error) ? error.GetString() : null)
            {
                case "authorization_pending":
                    continue;

                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;

                case "access_denied":
                    throw new CliException("The sign-in was refused. Nothing was granted.");

                case "expired_token":
                    throw new CliException(
                        "The code expired before it was approved. Run the command again to get a new one.");

                default:
                    throw new CliException(DescribeTokenError(body,
                        $"The installation refused the device code ({(int)response.StatusCode})."));
            }
        }
    }

    /// <summary>An OAuth error body reads as <c>error</c>/<c>error_description</c>, not RFC 7807.</summary>
    private static string DescribeTokenError(string body, string fallback)
    {
        try
        {
            var json = JsonDocument.Parse(body).RootElement;
            var error = json.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = json.TryGetProperty("error_description", out var d) ? d.GetString() : null;

            return (error, description) switch
            {
                (null, null) => fallback,
                (_, null) => $"{fallback} [{error}]",
                _ => $"{description} [{error}]",
            };
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

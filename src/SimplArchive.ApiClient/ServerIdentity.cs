using System;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.ApiClient;

/// <summary>What checking an address found.</summary>
public enum ServerCheckOutcome
{
    /// <summary>It answers as SimplArchive's API root.</summary>
    Found,

    /// <summary>Something answered, but not as SimplArchive (another site, an error page, a proxy).</summary>
    NotSimplArchive,

    /// <summary>Nothing answered: no route, no name, a timeout, a refused connection.</summary>
    Unreachable,

    /// <summary>The device refused this app the network (a per-app network permission switched off).</summary>
    NetworkNotAllowed,
}

/// <summary>The outcome, with a technical diagnosis for the log (never shown as the user's sentence).</summary>
public sealed record ServerCheck(ServerCheckOutcome Outcome, string? Diagnosis = null)
{
    public bool IsSimplArchive => Outcome == ServerCheckOutcome.Found;

    /// <summary>The localization key of the sentence a client shows for this outcome.</summary>
    public string MessageKey => Outcome switch
    {
        ServerCheckOutcome.Found => "SmFound",
        ServerCheckOutcome.NotSimplArchive => "SmNotSimplArchive",
        ServerCheckOutcome.NetworkNotAllowed => "SmNetworkDenied",
        _ => "SmUnreachable",
    };
}

// "Is this our server?" — an unauthenticated probe of a URL's API root (ADR "Desktop server configuration",
// issue #270). GETs `<url>/api` and confirms the response is SimplArchive's own HATEOAS discovery document
// (ADR "API discoverability / root endpoint design") rather than merely reachable. Used by the server managers' live
// URL check and before a sign-in.
public static class ServerIdentity
{
    public static async Task<bool> IsSimplArchiveAsync(string baseUrl, CancellationToken cancellationToken = default) =>
        (await CheckAsync(baseUrl, cancellationToken)).IsSimplArchive;

    /// <summary>
    /// Checks the address and says WHY it is not SimplArchive, when it is not. It once answered a plain false for
    /// every failure, and a phone whose network permission was off (GrapheneOS grants it at install, not when an
    /// update first asks) read "No connection to the server" while the server was fine. A fall-through says so: every
    /// failure is logged at Warning with its detail.
    /// </summary>
    public static async Task<ServerCheck> CheckAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        ServerCheck check;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api", cancellationToken);
            check = !resp.IsSuccessStatusCode
                ? new ServerCheck(ServerCheckOutcome.NotSimplArchive, $"HTTP {(int)resp.StatusCode}")
                : LooksLikeApiRoot(await resp.Content.ReadAsStringAsync(cancellationToken))
                    ? new ServerCheck(ServerCheckOutcome.Found)
                    : new ServerCheck(ServerCheckOutcome.NotSimplArchive, "the answer is not a SimplArchive API root");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;   // the caller moved on (another keystroke); not a finding about the server
        }
        catch (Exception e)
        {
            check = Classify(e);
        }

        if (!check.IsSimplArchive)
        {
            ApiClientSettings.Logger.LogWarning("{Address} is not usable as a SimplArchive server: {Outcome} ({Diagnosis})",
                baseUrl, check.Outcome, check.Diagnosis);
        }

        return check;
    }

    /// <summary>
    /// What a failed request means. A denied network surfaces as an access-denied socket error, or, through Android's
    /// own HTTP stack, only as the Java exception's text ("EACCES (Permission denied)"), so both are recognised.
    /// </summary>
    public static ServerCheck Classify(Exception e)
    {
        var chain = new List<Exception>();
        for (var x = e; x is not null; x = x.InnerException)
        {
            chain.Add(x);
        }

        var denied = chain.OfType<SocketException>().Any(s => s.SocketErrorCode == SocketError.AccessDenied)
            || chain.Any(x => x.Message.Contains("EACCES", StringComparison.Ordinal)
                || x.Message.Contains("Permission denied", StringComparison.OrdinalIgnoreCase));
        return new ServerCheck(denied ? ServerCheckOutcome.NetworkNotAllowed : ServerCheckOutcome.Unreachable,
            $"{e.GetType().Name}: {e.Message}");
    }

    // The discovery document is our root iff it carries a `links` array with the self link to `/api` plus the
    // `repositories` and `openIdConfiguration` links — a shape generic "reachable JSON" won't have, so a foreign
    // server can't match.
    public static bool LooksLikeApiRoot(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var hasSelf = false;
            var hasRepositories = false;
            var hasOidc = false;
            foreach (var link in links.EnumerateArray())
            {
                var rel = link.TryGetProperty("rel", out var r) ? r.GetString() : null;
                var href = link.TryGetProperty("href", out var h) ? h.GetString() : null;
                if (rel == "self" && href == "/api")
                {
                    hasSelf = true;
                }

                if (rel == "repositories")
                {
                    hasRepositories = true;
                }

                if (rel == "openIdConfiguration")
                {
                    hasOidc = true;
                }
            }

            return hasSelf && hasRepositories && hasOidc;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SimplArchive.DesktopClient.Services;

// A lightweight, unauthenticated "is the server reachable?" probe (ADR "Desktop session reconnect"): a GET of
// the OIDC discovery document, bounded by a 10s timeout, swallowing any failure to a plain false. Shared by
// the logon window's connection check and the background session heartbeat / reconnect flow.
public static class ServerReachability
{
    public static async Task<bool> CheckAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/.well-known/openid-configuration", cancellationToken);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

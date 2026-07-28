using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;

namespace SimplArchive.Client.Services;

// Starts/stops impersonation (ADR "User impersonation"): exchanges the admin's own access token (RFC 8693) for
// one representing the target user, stores it in ImpersonationState, and force-reloads so the whole app re-loads
// as the impersonated user. Stop clears the token and reloads back to the admin's session.
public class ImpersonationService
{
    private readonly IAccessTokenProvider _tokens;
    private readonly ImpersonationState _state;
    private readonly NavigationManager _nav;

    public ImpersonationService(IAccessTokenProvider tokens, ImpersonationState state, NavigationManager nav)
    {
        _tokens = tokens;
        _state = state;
        _nav = nav;
    }

    // Returns false if the admin's token can't be obtained or the exchange is refused (e.g. the target is an admin).
    public async Task<bool> StartAsync(Guid targetUserId)
    {
        var tokenResult = await _tokens.RequestAccessToken();
        if (!tokenResult.TryGetToken(out var adminToken))
        {
            return false;
        }

        using var http = new HttpClient { BaseAddress = new Uri(_nav.BaseUri) };
        var response = await http.PostAsync("connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
            ["client_id"] = "blazor-client",
            ["subject_token"] = adminToken.Value,
            ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
            ["requested_subject"] = targetUserId.ToString(),
        }));
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        await _state.StartAsync(json.GetProperty("access_token").GetString()!);
        _nav.NavigateTo("", forceLoad: true);
        return true;
    }

    public async Task StopAsync()
    {
        await _state.StopAsync();
        _nav.NavigateTo("", forceLoad: true);
    }
}

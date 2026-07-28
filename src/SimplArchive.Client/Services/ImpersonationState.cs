using Microsoft.JSInterop;

namespace SimplArchive.Client.Services;

// Holds the current impersonation access token (ADR "User impersonation") for the web client, backed by
// sessionStorage so an impersonation session survives a page reload (until Stop or the tab closes). The token,
// when set, is attached to API calls by ImpersonationHandler in place of the admin's own token.
public class ImpersonationState
{
    private const string StorageKey = "simplarchive.impersonation";
    private readonly IJSRuntime _js;

    public ImpersonationState(IJSRuntime js) => _js = js;

    // Read synchronously by the message handler on every request — kept in memory, mirrored to sessionStorage.
    public string? Token { get; private set; }

    public bool IsImpersonating => Token is not null;

    // Loaded once at startup (MainLayout) so a reload restores the impersonation session.
    public async Task InitializeAsync() => Token = await _js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);

    public async Task StartAsync(string token)
    {
        Token = token;
        await _js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, token);
    }

    public async Task StopAsync()
    {
        Token = null;
        await _js.InvokeVoidAsync("sessionStorage.removeItem", StorageKey);
    }
}

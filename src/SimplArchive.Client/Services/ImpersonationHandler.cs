using System.Net.Http.Headers;

namespace SimplArchive.Client.Services;

// The inner-most handler on the Api HttpClient (ADR "User impersonation"). The outer AuthorizationMessageHandler
// has already attached the admin's own token; when an impersonation session is active this overrides it with the
// impersonation token, so every API call acts as the impersonated user. A no-op when not impersonating.
public class ImpersonationHandler : DelegatingHandler
{
    private readonly ImpersonationState _state;

    public ImpersonationHandler(ImpersonationState state) => _state = state;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_state.Token is { } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

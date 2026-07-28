using Serilog.Context;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Api.Logging;

/// <summary>
/// Establishes a correlation id for the request and pushes it into the Serilog <see cref="LogContext"/> so
/// <em>every</em> log written during the request carries it (ADR "Enterprise-grade structured logging with
/// Serilog"). The id comes from an inbound <c>X-Correlation-ID</c> / W3C <c>traceparent</c> header when present
/// (so a caller's/tracing system's id flows through), else the ambient <see cref="System.Diagnostics.Activity"/>
/// id, else a fresh GUID. It's echoed back on the response so a client/operator can quote it. Runs early, ahead
/// of auth, so auth and OpenIddict logs are correlated too.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolveCorrelationId(context);
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var provided) && !string.IsNullOrWhiteSpace(provided))
        {
            return provided.ToString();
        }

        return System.Diagnostics.Activity.Current?.Id ?? Guid.NewGuid().ToString("N");
    }
}

/// <summary>
/// Pushes the resolved tenant + principal into the Serilog <see cref="LogContext"/> for the rest of the
/// pipeline, so controller logs carry <c>TenantId</c> / <c>UserId</c> / <c>ServiceAccountId</c> without each
/// call site having to add them. Placed <em>after</em> <c>CurrentPrincipalMiddleware</c> (which populates the
/// accessors from the validated token), so a value is present only once the caller is known.
/// </summary>
public sealed class PrincipalLogContextMiddleware
{
    private readonly RequestDelegate _next;

    public PrincipalLogContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(
        HttpContext context,
        ICurrentTenantAccessor tenant,
        ICurrentUserAccessor user,
        ICurrentServiceAccountAccessor serviceAccount,
        ICurrentPlatformAdministratorAccessor platformAdministrator)
    {
        var disposables = new List<IDisposable>(4);
        if (tenant.TenantId is { } tenantId) disposables.Add(LogContext.PushProperty("TenantId", tenantId));
        if (user.UserId is { } userId) disposables.Add(LogContext.PushProperty("UserId", userId));
        if (serviceAccount.ServiceAccountId is { } saId) disposables.Add(LogContext.PushProperty("ServiceAccountId", saId));
        if (platformAdministrator.PlatformAdministratorId is { } paId) disposables.Add(LogContext.PushProperty("PlatformAdministratorId", paId));

        try
        {
            await _next(context);
        }
        finally
        {
            for (var i = disposables.Count - 1; i >= 0; i--)
            {
                disposables[i].Dispose();
            }
        }
    }
}

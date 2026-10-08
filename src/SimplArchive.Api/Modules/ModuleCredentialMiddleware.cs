using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Controllers;
using SimplArchive.Api.Errors.Exceptions.Modules;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Authenticates protocol clients on a module's claimed ROOT routes (ABI 1.7, core ADR 0898): a package manager's
/// Basic auth, a bearer token or an API-key header, where there is no login and no ambient tenant.
/// </summary>
/// <remarks>
/// <para>
/// Runs between authentication and authorization. On a claimed route it is EXCLUSIVE: whatever the core's own login
/// produced is discarded, so a bearer token there is always a module credential.
/// </para>
/// <para>
/// The order is the security argument: the credential names its tenant (<see cref="ModuleCredentialFormat"/>), the
/// tenant is set, the activation gate runs, the request starts acting as the module's own principal, and only then
/// is the module handed the secret. Every facade read in the request is therefore consent-gated by the grants that
/// principal holds, so a stolen credential reads nothing the module was not granted.
/// </para>
/// <para>
/// A refusal answers <c>401</c> with the module's <c>WWW-Authenticate</c> challenge and no body: the counterparty is
/// a protocol client, which retries with credentials on exactly that. A request with NO credential is the protocol's
/// ordinary first attempt and is logged at Debug; a credential that is refused is a Warning (ADR 0626).
/// </para>
/// </remarks>
public sealed class ModuleCredentialMiddleware(
    RequestDelegate next, IReadOnlyList<ModuleLoader.LoadedModule> modules, ILogger<ModuleCredentialMiddleware> logger)
{
    /// <summary>The authentication type of a module-credential principal; the core's own principal middleware skips it.</summary>
    public const string AuthenticationType = "ModuleCredential";

    private readonly Dictionary<string, ModuleLoader.LoadedModule> _byPrefix = modules
        .SelectMany(m => m.Module.RootRoutePrefixes.Select(p => (Prefix: p, Module: m)))
        .ToDictionary(x => x.Prefix, x => x.Module, StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (_byPrefix.Count == 0 || !_byPrefix.TryGetValue(FirstSegment(path), out var owner))
        {
            await next(context);
            return;
        }

        // A claimed root is the module's alone: never the core's login, and never the web client's fallback page.
        context.User = new ClaimsPrincipal(new ClaimsIdentity());
        var ownAssembly = owner.Module.GetType().Assembly;
        if (context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
            || action.ControllerTypeInfo.Assembly != ownAssembly)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var authenticator = context.RequestServices.GetServices<IModuleCredentialAuthenticator>()
            .FirstOrDefault(a => a.GetType().Assembly == ownAssembly && a.RoutePrefixes.Any(p => Covers(p, path)));
        if (authenticator is null)
        {
            await next(context);   // a claimed route with no authenticator: anonymous, and the activation gate refuses it
            return;
        }

        var moduleId = owner.Module.ModuleId;
        // A credential the path carried (ADR 0909) was cut out by PathCredentialMiddleware; otherwise the headers.
        var (scheme, credential) = context.Features.Get<PathCredentialFeature>() is { } carried
            ? ("Path", carried.Credential)
            : Presented(context.Request, authenticator.ApiKeyHeaders);
        logger.LogTrace("Module {ModuleId} credential exchange on {Path}: presented {Scheme}", moduleId, path, scheme ?? "nothing");
        if (credential is null)
        {
            logger.LogDebug("Challenged an anonymous request to module {ModuleId} on {Path}", moduleId, path);
            Challenge(context, authenticator.Challenge);
            return;
        }

        if (!ModuleCredentialFormat.TryParse(credential, out var tenantId, out var secret))
        {
            Refuse(context, authenticator.Challenge, moduleId, path, scheme!, "it is not in the core's credential format");
            return;
        }

        // The tenant the credential names, then the gate, then the module's own principal: the same order and the
        // same refusal as every module route, before the module sees anything.
        var services = context.RequestServices;
        ((CurrentTenantAccessor)services.GetRequiredService<ICurrentTenantAccessor>()).TenantId = tenantId;
        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();
        var principal = await ModuleActivationCheck.IsActiveAsync(dbContext, moduleId, DateTimeOffset.UtcNow, context.RequestAborted)
            ? await ModulePrincipal.FindAsync(dbContext, moduleId, context.RequestAborted)
            : null;
        if (principal is null)
        {
            throw new ModuleNotActiveException(moduleId);
        }

        ((CurrentServiceAccountAccessor)services.GetRequiredService<ICurrentServiceAccountAccessor>()).ServiceAccountId = principal.Id;
        services.GetRequiredService<ModuleIdentityAccessor>().ModuleId = moduleId;

        var identity = await authenticator.AuthenticateAsync(secret, context.RequestAborted);
        if (identity is null)
        {
            Refuse(context, authenticator.Challenge, moduleId, path, scheme!, "the module does not recognise it");
            return;
        }

        ((ModuleCredentialContext)services.GetRequiredService<IModuleCredentialContext>()).Identity = identity;
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, identity.PrincipalLabel)], AuthenticationType));
        logger.LogDebug("Module {ModuleId} authenticated {ModuleCredential} by {Scheme} on {Path}", moduleId, identity.PrincipalLabel, scheme, path);

        using (logger.BeginScope(new Dictionary<string, object> { ["ModuleCredential"] = identity.PrincipalLabel }))
        {
            await next(context);
        }
    }

    /// <summary>The credential the request presents and how: the Basic password, a Bearer token, or the first
    /// declared API-key header. The Basic user name is ignored (core #1552).</summary>
    public static (string? Scheme, string? Credential) Presented(HttpRequest request, IReadOnlyList<string> apiKeyHeaders)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
                var colon = decoded.IndexOf(':');
                return ("Basic", colon < 0 ? null : NullIfEmpty(decoded[(colon + 1)..]));
            }
            catch (FormatException)
            {
                return ("Basic", null);
            }
        }

        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return ("Bearer", NullIfEmpty(authorization["Bearer ".Length..].Trim()));
        }

        foreach (var header in apiKeyHeaders)
        {
            if (NullIfEmpty(request.Headers[header].ToString()) is { } key)
            {
                return (header, key);
            }
        }

        return (null, null);
    }

    private void Refuse(HttpContext context, ModuleChallenge challenge, string moduleId, string path, string scheme, string reason)
    {
        logger.LogWarning(
            "Refused a {Scheme} credential for module {ModuleId} on {Path}: {Reason}. Trace carries the exchange.",
            scheme, moduleId, path, reason);
        Challenge(context, challenge);
    }

    private static void Challenge(HttpContext context, ModuleChallenge challenge)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"{challenge.Scheme} realm=\"{challenge.Realm}\"";
    }

    /// <summary>Whether an authenticator's prefix covers the path, on whole segments (<c>nuget</c> covers
    /// <c>/nuget/v3</c>, never <c>/nugetx</c>).</summary>
    public static bool Covers(string prefix, string path)
    {
        var p = prefix.Trim('/');
        var trimmed = path.TrimStart('/');
        return p.Length > 0
            && trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            && (trimmed.Length == p.Length || trimmed[p.Length] == '/');
    }

    private static string FirstSegment(string path)
    {
        var trimmed = path.TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

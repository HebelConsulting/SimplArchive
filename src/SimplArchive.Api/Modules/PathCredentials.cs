using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// The root prefixes whose credential travels in the path (ABI 1.11, ADR 0909): <c>/fdroid/&lt;credential&gt;/…</c>.
/// Filled once at startup from the loaded modules' authenticators, and read by the middleware that cuts the
/// credential out and by the log redactor.
/// </summary>
public sealed class PathCredentialPrefixes
{
    private HashSet<string> _prefixes = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => _prefixes.Count == 0;

    public bool Contains(string prefix) => _prefixes.Contains(prefix);

    /// <summary>
    /// Reads each module's declarations. A prefix the declaring module did not claim as a root prefix is refused
    /// with an Error naming it: honouring it would let one module strip a segment out of another's paths.
    /// </summary>
    public void Load(IServiceProvider services, IReadOnlyList<ModuleLoader.LoadedModule> modules, ILogger logger)
    {
        if (modules.All(m => m.Module.RootRoutePrefixes.Count == 0))
        {
            return;
        }

        using var scope = services.CreateScope();
        IReadOnlyList<IModuleCredentialAuthenticator> authenticators;
        try
        {
            authenticators = [.. scope.ServiceProvider.GetServices<IModuleCredentialAuthenticator>()];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A module must never take the host down (#1147): its path credentials stay off, and it says why.
            logger.LogError(e, "The modules' credential authenticators could not be constructed at startup, so no path "
                + "carries a credential on this host; requests there are refused (core ADR 0909).");
            return;
        }

        var loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var authenticator in authenticators)
        {
            var owner = modules.FirstOrDefault(m => m.Module.GetType().Assembly == authenticator.GetType().Assembly);
            foreach (var prefix in authenticator.PathCredentialPrefixes)
            {
                if (owner is not null && owner.Module.RootRoutePrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
                {
                    loaded.Add(prefix);
                    continue;
                }

                logger.LogError(
                    "Module {ModuleId} declares a credential in the path of '{Prefix}', a root prefix it did not claim. "
                    + "Ignored: requests there carry no path credential (core ADR 0909).",
                    owner?.Module.ModuleId ?? authenticator.GetType().Assembly.GetName().Name, prefix);
            }
        }

        _prefixes = loaded;
    }

    /// <summary>The credential segment of a RAW path on a path-credential prefix, or null.</summary>
    public string? CredentialIn(PathString path)
    {
        if (IsEmpty || path.Value is not { Length: > 1 } value)
        {
            return null;
        }

        var segments = value.Split('/', 4, StringSplitOptions.None);   // "", prefix, credential, rest
        return segments.Length >= 3 && Contains(segments[1]) && segments[2].Length > 0 ? segments[2] : null;
    }
}

/// <summary>The credential the path carried, kept for the authenticator after the path was shortened (ADR 0909).</summary>
public sealed record PathCredentialFeature(string Credential, string Prefix);

/// <summary>
/// The FIRST middleware (ADR 0909): on a path-credential prefix it cuts the credential segment out of the path, so
/// routing, the module and every later log see <c>/fdroid/repo/…</c>, and keeps it in
/// <see cref="PathCredentialFeature"/> for <see cref="ModuleCredentialMiddleware"/>.
/// </summary>
public sealed class PathCredentialMiddleware(RequestDelegate next, PathCredentialPrefixes prefixes)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (prefixes.CredentialIn(context.Request.Path) is { } credential)
        {
            var value = context.Request.Path.Value!;
            var prefix = value.Split('/', 3)[1];
            var rest = value[(prefix.Length + credential.Length + 2)..];
            context.Features.Set(new PathCredentialFeature(credential, prefix));
            context.Request.Path = new PathString($"/{prefix}{rest}");
        }

        return next(context);
    }

    /// <summary>The path as a log line should show it: the prefix, <c>***</c> where the credential was, then the rest.</summary>
    public static string DisplayPath(HttpContext context) =>
        context.Features.Get<PathCredentialFeature>() is { } carried
            ? $"/{carried.Prefix}/***{context.Request.Path.Value![(carried.Prefix.Length + 1)..]}"
            : context.Request.Path.Value ?? string.Empty;
}

using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>
/// Where a module may put its routes (ABI 1.7, core ADR 0898): under <c>api/</c>, or under a ROOT prefix it claimed
/// from the core's allowlist. Checked at startup, BEFORE a module's services are registered, so a module that breaks
/// the rule is refused whole, with an Error naming why, rather than shipping a route that silently loses.
/// </summary>
public static class ModuleRoutes
{
    /// <summary>
    /// The root-level prefixes a module may claim. Each entry is a decision the core made, which is the point: a free
    /// claim would let a module shadow <c>/connect</c>, <c>/health</c> or <c>/Account/Login</c>, and the failure
    /// would be a working-looking installation whose login or readiness probe answers something else. Adding one is a
    /// core change with an ADR line.
    /// </summary>
    /// <remarks>
    /// <c>nuget</c>: the Licensing Module's package feed (core #1550). Its address is pasted into every customer's
    /// NuGet configuration, so it must not carry <c>/api/modules/…</c>.
    /// <c>fdroid</c>: the Licensing Module's Android app repository (core #1649). Its address is pasted into an app
    /// store client, which sends no authentication, so the credential rides in the path (ADR 0909).
    /// </remarks>
    public static readonly IReadOnlyList<string> AllowedRootPrefixes = ["nuget", "fdroid"];

    /// <summary>The modules whose route claims and controller routes are admissible, in load order. A refused module
    /// is dropped before anything of it is registered; the first claimant of a prefix keeps it.</summary>
    public static IReadOnlyList<ModuleLoader.LoadedModule> Admit(IReadOnlyList<ModuleLoader.LoadedModule> modules, ILogger logger)
    {
        var admitted = new List<ModuleLoader.LoadedModule>();
        var claimedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var loaded in modules)
        {
            string? refusal;
            IReadOnlyList<string> prefixes = [];
            try
            {
                prefixes = loaded.Module.RootRoutePrefixes;
                refusal = Refusal(loaded.Module.ModuleId, prefixes, claimedBy, ControllerTypes(loaded.Module.GetType().Assembly));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                refusal = $"its routes could not be inspected ({e.GetType().Name}: {e.Message})";
            }

            if (refusal is not null)
            {
                logger.LogError(
                    "Module {ModuleId} ({Path}) is NOT active on this host: {Refusal}. Its masks, machines and endpoints are "
                    + "absent; the rest of the application is unaffected (core ADR 0898).",
                    loaded.Module.ModuleId, loaded.AssemblyPath, refusal);
                continue;
            }

            foreach (var prefix in prefixes)
            {
                claimedBy[prefix] = loaded.Module.ModuleId;
            }

            admitted.Add(loaded);
        }

        return admitted;
    }

    /// <summary>Why a module's route claims are refused, or null when they are admissible.</summary>
    public static string? Refusal(
        string moduleId, IReadOnlyList<string> claimedPrefixes, IReadOnlyDictionary<string, string> claimedByEarlier,
        IEnumerable<Type> controllerTypes)
    {
        foreach (var prefix in claimedPrefixes)
        {
            if (!AllowedRootPrefixes.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                return $"it claims the root route prefix '{prefix}', which is not on the core's allowlist "
                    + $"({string.Join(", ", AllowedRootPrefixes)})";
            }

            if (claimedByEarlier.TryGetValue(prefix, out var owner))
            {
                return $"it claims the root route prefix '{prefix}', which module {owner} already holds";
            }
        }

        var outside = RouteTemplates(controllerTypes)
            .FirstOrDefault(t => !IsUnderApi(t) && !claimedPrefixes.Contains(FirstSegment(t), StringComparer.OrdinalIgnoreCase));
        return outside is null
            ? null
            : $"a controller routes to '{outside}', which is neither under 'api/' nor under a root prefix the module claimed";
    }

    /// <summary>Every attribute-route template the controllers declare, class and action combined as MVC combines
    /// them. A controller with no attribute route at all is unreachable (no conventional routes are mapped) and
    /// contributes nothing.</summary>
    public static IEnumerable<string> RouteTemplates(IEnumerable<Type> controllerTypes)
    {
        foreach (var type in controllerTypes)
        {
            var classTemplates = Templates(type).OfType<string>().DefaultIfEmpty(string.Empty).ToList();
            // An action's [HttpGet] WITHOUT a template inherits the controller's route, so it counts as "".
            var actions = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => Templates(m).Select(t => t ?? string.Empty).ToList())
                .Where(t => t.Count > 0)
                .ToList();
            if (actions.Count == 0)
            {
                foreach (var t in classTemplates.Where(t => t.Length > 0))
                {
                    yield return t;
                }

                continue;
            }

            foreach (var action in actions.SelectMany(a => a))
            {
                if (action.StartsWith('/') || action.StartsWith("~/", StringComparison.Ordinal))
                {
                    yield return action.TrimStart('~', '/');
                    continue;
                }

                foreach (var cls in classTemplates)
                {
                    var combined = string.Join('/', new[] { cls.TrimStart('~', '/'), action }.Where(s => s.Length > 0));
                    if (combined.Length > 0)
                    {
                        yield return combined;
                    }
                }
            }
        }
    }

    private static IEnumerable<string?> Templates(MemberInfo member) =>
        member.GetCustomAttributes(inherit: true).OfType<IRouteTemplateProvider>().Select(p => p.Template);

    private static IEnumerable<Type> ControllerTypes(Assembly assembly) =>
        assembly.GetExportedTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t));

    private static bool IsUnderApi(string template) =>
        string.Equals(FirstSegment(template), "api", StringComparison.OrdinalIgnoreCase);

    private static string FirstSegment(string template)
    {
        var trimmed = template.TrimStart('~', '/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[..slash];
    }
}

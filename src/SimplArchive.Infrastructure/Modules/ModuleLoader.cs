using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Loads industry modules from the <c>Modules/</c> directory at startup (ADR 0741): one non-collectible
/// <see cref="AssemblyLoadContext"/> per module, so modules' dependencies are isolated from each other and
/// from the core while ABI types resolve from the default context and stay shared. Deactivation is logical
/// (ADR 0740); adding or removing module FILES takes a restart, by design — collectible hot-unload was
/// rejected as the finicky corner that pins on any rooted delegate.
/// </summary>
public static class ModuleLoader
{
    /// <summary>A module the host accepted: its contract, where it came from, and WHICH BUILD it is.</summary>
    /// <param name="Build">
    /// The assembly's informational version — in practice <c>1.0.0+&lt;git sha&gt;</c>, because SourceLink
    /// stamps the source commit and no module declares a <c>&lt;Version&gt;</c> of its own.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The version NUMBER is worthless and the SHA is the identity</b>, which is worth saying out loud before
    /// anyone renders this as "v1.0.0". Every module builds as <c>1.0.0</c> — the csproj declares nothing, so
    /// that is the SDK default and it is identical for every module and every build. What distinguishes one
    /// build from the next is the <c>+sha</c> suffix. When modules become versioned packages (#1246) the number
    /// starts meaning something; until then, do not trust it.
    /// </para>
    /// <para>
    /// <b>Why capture it at all:</b> the kiosk ran a module build four releases old and nothing could say so
    /// (#1242). A stale module loads, seeds its masks, registers its controllers and answers requests — only
    /// features added after the deployed build are missing, which reads as "never built" rather than "not
    /// deployed". The investigation ended up comparing file mtimes and SHA-256 sums because the process itself
    /// could not answer "which build is this?". It can now.
    /// </para>
    /// </remarks>
    public sealed record LoadedModule(IIndustryModule Module, string AssemblyPath, string? Build = null);

    /// <summary>
    /// Scans <paramref name="modulesDirectory"/> for module assemblies — every <c>*.dll</c> in each
    /// immediate subdirectory (a module ships as a folder: its assembly plus its private dependencies) and
    /// any loose <c>*.dll</c> at the top level. Missing directory → no modules, silently: most
    /// deployments carry none, and an empty mount must not warn.
    /// </summary>
    public static IReadOnlyList<LoadedModule> LoadAll(string modulesDirectory, ILogger logger)
    {
        if (!Directory.Exists(modulesDirectory))
        {
            return [];
        }

        var loaded = new List<LoadedModule>();
        foreach (var candidate in CandidateAssemblies(modulesDirectory))
        {
            try
            {
                var context = new ModuleLoadContext(candidate);
                var assembly = context.LoadFromAssemblyName(AssemblyName.GetAssemblyName(candidate));
                foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && typeof(IIndustryModule).IsAssignableFrom(t)))
                {
                    if (Activator.CreateInstance(type) is not IIndustryModule module)
                    {
                        continue;
                    }

                    if (!MinorCompatible(module.AbiMinorVersion))
                    {
                        // A module built against a NEWER minor may call members this host does not have. Left
                        // unchecked that surfaces as a MissingMethodException from a static initializer,
                        // which killed the host outright before #1147. Refused here instead, the same way
                        // and for the same reason as a major mismatch.
                        logger.LogWarning(
                            "Module {ModuleId} ({Path}) was built against ABI {ModuleMajor}.{ModuleMinor}; this host provides "
                            + "{HostMajor}.{HostMinor}. The module is NOT loaded — a module may be OLDER than its host but "
                            + "never newer. Upgrade SimplArchive, or install a module built against this host's ABI.",
                            module.ModuleId, candidate, module.AbiMajorVersion, module.AbiMinorVersion,
                            ModuleAbiVersion.Major, ModuleAbiVersion.Minor);
                        continue;
                    }

                    if (!AbiCompatible(module.AbiMajorVersion))
                    {
                        // The version gate is load-time and self-explaining (ADR 0741): an admin message,
                        // not a stack trace — the module stays inactive, the tenant's data untouched.
                        logger.LogWarning(
                            "Module {ModuleId} ({Path}) was built against ABI major {ModuleMajor}; this host provides {HostMajor}. "
                            + "The module is NOT loaded — install a build matching this host's ABI major.",
                            module.ModuleId, candidate, module.AbiMajorVersion, ModuleAbiVersion.Major);
                        continue;
                    }

                    // The BUILD, not just the path: a path says where the file is, never which one it is.
                    var build = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
                        ?.InformationalVersion;

                    if (!ReadModelsAreConstructible(module, candidate, logger))
                    {
                        continue;
                    }

                    WarnAboutUnusableSettings(module, logger);

                    logger.LogInformation("Loaded module {ModuleId} ({DisplayName}) build {Build} from {Path}.",
                        module.ModuleId, module.DisplayName, build ?? "unknown", candidate);
                    loaded.Add(new LoadedModule(module, candidate, build));
                }
            }
            catch (BadImageFormatException)
            {
                // A native or otherwise unloadable dll in a module folder (a vendored dependency) — not a
                // module, not an error.
            }
            catch (Exception e)
            {
                // A broken module must not take the host down; it must also not fail silently (ADR 0626).
                logger.LogWarning(e, "Module assembly {Path} could not be loaded and was skipped.", candidate);
            }
        }

        return loaded;
    }

    /// <summary>
    /// Refuses a module whose declared read-model context the host cannot construct (#1475), naming it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Refused rather than warned-and-loaded</b>, unlike an unusable setting below. The difference is what
    /// the defect costs: a <c>Choice</c> with no choices breaks one form control, while a context the host
    /// cannot construct can never be MIGRATED — so its tables do not exist, every projection write fails, and
    /// a rebuild cannot help. Half a module whose read model is missing is worse than none, and this is the
    /// same judgement the ABI-version gate above makes for the same reason.
    /// </para>
    /// <para>
    /// <b>Said at load because the alternative is said at a customer's migration.</b> The host constructs a
    /// module context by reflection on exactly one path — the owner connection (ADR 0721) — which no test
    /// suite runs, so the first report of this was a <c>MissingMethodException</c> from inside
    /// <c>Activator</c> during <c>db-migrate</c>, with nothing naming the module and everything pointing at
    /// the core. The predicate lives beside the construction it mirrors
    /// (<see cref="ModuleReadModelWiring.CanHostConstruct"/>) so the two cannot drift.
    /// </para>
    /// </remarks>
    public static bool ReadModelsAreConstructible(IIndustryModule module, string path, ILogger logger)
    {
        foreach (var set in module.ReadModels.Where(s => !ModuleReadModelWiring.CanHostConstruct(s.ContextType)))
        {
            logger.LogWarning(
                "Module {ModuleId} ({Path}) declares read-model context {ContextType}, which this host cannot "
                + "construct. The module is NOT loaded — its schema could never be migrated, so its projections "
                + "would fail at every write. The context needs ONE public constructor taking either "
                + "DbContextOptions<{ContextName}> or DbContextOptions, and nothing else; a constructor with "
                + "further parameters cannot be used, because the host builds the context itself to migrate it.",
                module.ModuleId, path, set.ContextType.FullName, set.ContextType.Name);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Names any setting the host will be unable to offer — currently a <c>Choice</c> declaring no
    /// <see cref="ModuleSetting.Choices"/> (ABI 0.29). The module still loads: an unusable form control is not
    /// a reason to withhold its masks, its controllers and its state machines.
    /// </summary>
    /// <remarks>
    /// Said ONCE, at load, rather than on every settings read — the defect is a property of the build, so a
    /// per-request warning would be the same sentence thousands of times. And said at all because the failure
    /// is otherwise invisible in both directions (ADR 0626): the form renders a chooser with nothing to choose,
    /// and every value a tenant tries is refused by the validation that compares against the empty list. The
    /// administrator would conclude the setting is broken by the CORE, with nothing pointing at the module.
    /// </remarks>
    public static void WarnAboutUnusableSettings(IIndustryModule module, ILogger logger)
    {
        foreach (var setting in module.Settings.Where(
            s => s.Kind == ModuleSettingKind.Choice && s.Choices.Count == 0))
        {
            logger.LogWarning(
                "Module {ModuleId} declares setting '{Key}' as a Choice but offers no choices. The form can offer "
                + "nothing and every value will be refused — the module must declare its permitted values. The "
                + "rest of the module is loaded normally.",
                module.ModuleId, setting.Key);
        }
    }

    /// <summary>The compat rule, its own method so the refusal is testable without loading anything:
    /// major locks (ADR 0741).</summary>
    public static bool AbiCompatible(int moduleAbiMajor) => moduleAbiMajor == ModuleAbiVersion.Major;

    /// <summary>
    /// Whether a module's ABI MINOR is servable by this host: anything up to and including our own (#1147).
    /// </summary>
    /// <remarks>
    /// Asymmetric on purpose, and that asymmetry IS "minor floats": an older module asks only for members
    /// this host still has, while a newer one may ask for members that do not exist here. The first is the
    /// compatibility the ADR promises; the second is the crash it did not prevent.
    /// </remarks>
    public static bool MinorCompatible(int moduleAbiMinor) => moduleAbiMinor <= ModuleAbiVersion.Minor;

    private static IEnumerable<string> CandidateAssemblies(string modulesDirectory)
    {
        foreach (var dll in Directory.EnumerateFiles(modulesDirectory, "*.dll", SearchOption.TopDirectoryOnly))
        {
            yield return dll;
        }

        foreach (var dir in Directory.EnumerateDirectories(modulesDirectory))
        {
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                yield return dll;
            }
        }
    }

    // One context per module: the module's own dependencies resolve from its folder (the resolver reads its
    // .deps.json); anything it shares with the host — the ABI above all — falls through to the default
    // context, which is what makes IIndustryModule one type on both sides of the boundary.
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver? _resolver;

        public ModuleLoadContext(string modulePath)
            : base(isCollectible: false)
        {
            // A bare assembly with no .deps.json beside it is a legitimate single-file module — every
            // unresolved dependency then falls through to the default context.
            try
            {
                _resolver = new AssemblyDependencyResolver(modulePath);
            }
            catch (InvalidOperationException)
            {
                _resolver = null;
            }
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (string.Equals(assemblyName.Name, "SimplArchive.ModuleAbi", StringComparison.Ordinal))
            {
                return null; // the shared contract — default context, one type identity.
            }

            var path = _resolver?.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}

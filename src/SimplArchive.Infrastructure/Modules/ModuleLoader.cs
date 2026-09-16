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

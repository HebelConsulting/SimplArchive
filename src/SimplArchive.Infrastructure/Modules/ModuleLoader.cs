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
    /// <summary>A module the host accepted: its contract, where it came from, its context.</summary>
    public sealed record LoadedModule(IIndustryModule Module, string AssemblyPath);

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

                    logger.LogInformation("Loaded module {ModuleId} ({DisplayName}) from {Path}.", module.ModuleId, module.DisplayName, candidate);
                    loaded.Add(new LoadedModule(module, candidate));
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

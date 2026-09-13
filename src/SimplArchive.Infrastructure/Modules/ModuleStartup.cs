using Microsoft.Extensions.Logging;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Runs a module's startup seams so that a broken module costs its own features and never the host
/// (#1147).
/// </summary>
/// <remarks>
/// <para>
/// ADR 0741 promises that an incompatible module "stays inactive, the tenant's data untouched".
/// <see cref="ModuleLoader"/> honours that for the failures it can see — a bad assembly, a version mismatch —
/// but everything called AFTER it ran unguarded, straight on the host's construction path. So a module that
/// loaded fine and then threw took the whole API down, and because the container restarts, it crash-looped.
/// </para>
/// <para>
/// That is not hypothetical and it is not a demo-only risk. A module built against ABI 0.20 met a 0.21 host,
/// <c>DefineStateMachines</c> threw <c>MissingMethodException</c> on a record constructor that no longer
/// existed, and the public kiosk was down for 94 minutes. The log said <c>Loaded module flight-school</c>
/// one line above the fatal — because loading really had succeeded. Any customer upgrading the core with an
/// older module build gets the same, recoverable only by deleting the dll from the filesystem, which needs
/// the host they cannot start.
/// </para>
/// <para>
/// A refusal here is <b>Error</b>, not Warning: unlike the version gate's clean "install a matching build",
/// this is an unexpected fault an administrator must investigate — and the module's features are silently
/// absent until they do, which is exactly the case ADR 0626 says must not pass unremarked.
/// </para>
/// </remarks>
public static class ModuleStartup
{
    /// <summary>
    /// Runs <paramref name="seam"/> for a module, returning false when it threw.
    /// </summary>
    /// <remarks>
    /// Catches broadly ON PURPOSE. The point is not to handle the exceptions we predicted — it is that
    /// arbitrary third-party code runs here and the host must survive whatever it does. A narrower catch
    /// would be a list of the failures somebody already thought of, which is what left this unguarded.
    /// </remarks>
    public static bool TryRun(ModuleLoader.LoadedModule loaded, string seamName, ILogger logger, Action seam)
    {
        try
        {
            seam();
            return true;
        }
        catch (Exception e)
        {
            logger.LogError(
                e,
                "Module {ModuleId} ({Path}) failed in {Seam} and is NOT active on this host — its masks, "
                + "machines and endpoints are absent, and the rest of the application is unaffected. A "
                + "MissingMethodException here almost always means the module was built against a different "
                + "ABI version than this host provides ({AbiMajor}.x): rebuild it against this host's "
                + "SimplArchive.ModuleAbi, or remove it from the Modules directory.",
                loaded.Module.ModuleId, loaded.AssemblyPath, seamName, ModuleAbi.ModuleAbiVersion.Major);
            return false;
        }
    }
}

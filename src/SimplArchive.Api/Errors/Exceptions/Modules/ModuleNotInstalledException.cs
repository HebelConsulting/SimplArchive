using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>The named module is not among this host's loaded modules — never shipped here, removed, or
/// refused at load for an ABI-major mismatch (the loader's Warning carries which).</summary>
public sealed class ModuleNotInstalledException : ModuleException
{
    public ModuleNotInstalledException(string moduleId)
        : base("MODULE_NOT_INSTALLED", StatusCodes.Status404NotFound,
            $"No module '{moduleId}' is installed on this host.")
    {
    }
}

using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>
/// A settings write named a key the module never declared (ADR 0772).
/// </summary>
/// <remarks>
/// Refused rather than stored: a store that accepts any key is the free-form bag the declared design exists
/// to avoid, and a typo would be written happily and then never read back — a setting that looks configured
/// and does nothing.
/// </remarks>
public sealed class ModuleSettingNotDeclaredException : ModuleException
{
    public ModuleSettingNotDeclaredException(string moduleId, string key)
        : base("MODULE_SETTING_NOT_DECLARED", StatusCodes.Status400BadRequest,
            $"The module '{moduleId}' declares no setting '{key}'.")
    {
    }
}

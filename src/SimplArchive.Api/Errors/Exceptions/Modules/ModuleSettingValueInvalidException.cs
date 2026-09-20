using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>
/// A settings write sent a value the setting's kind cannot mean — a Boolean given something other than
/// <c>true</c> or <c>false</c> (ABI 0.27, ADR 0810).
/// </summary>
/// <remarks>
/// Refused for the same reason its sibling refuses an undeclared key: a third state nobody handles is worse
/// than a rejection. The read side is a straight equality test, so a stored "yes" or "1" would read as FALSE
/// at the moment it matters — leaving an administrator looking at a toggle that is on and a behaviour that
/// is off, with nothing anywhere saying why.
/// </remarks>
public sealed class ModuleSettingValueInvalidException : ModuleException
{
    public ModuleSettingValueInvalidException(string moduleId, string key)
        : base("MODULE_SETTING_VALUE_INVALID", StatusCodes.Status400BadRequest,
            $"The setting '{key}' of module '{moduleId}' is a yes/no and accepts only 'true' or 'false'.")
    {
    }
}

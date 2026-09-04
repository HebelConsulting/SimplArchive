using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>
/// A module route was called by a tenant whose activation is not active — never licensed, or lapsed past
/// grace (ADRs 0737/0740).
/// </summary>
/// <remarks>
/// 404, not 403, with the code naming the reason: per ADR 0543 the module's surface simply does not exist
/// for this tenant — its rels were never emitted, so a conforming client never asks — while an
/// administrator reading the wire still learns exactly which knob to turn. The sibling of
/// <see cref="ModuleNotInstalledException"/>: that one says the HOST does not carry the module, this one
/// that the TENANT has not (currently) licensed it.
/// </remarks>
public sealed class ModuleNotActiveException : ModuleException
{
    public ModuleNotActiveException(string moduleId)
        : base("MODULE_NOT_ACTIVE", StatusCodes.Status404NotFound,
            $"The module '{moduleId}' is not active for this tenant — file a valid license to activate it.")
    {
    }
}

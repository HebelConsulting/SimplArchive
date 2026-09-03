using Microsoft.AspNetCore.Http;

namespace SimplArchive.Api.Errors.Exceptions.Modules;

/// <summary>The presented license failed verification (ADR 0743) — the message carries the domain layer's
/// precise refusal (bad signature, wrong module, wrong tenant, ABI mismatch, unreadable artefact).</summary>
public sealed class ModuleLicenseRejectedException : ModuleException
{
    public ModuleLicenseRejectedException(string reason)
        : base("MODULE_LICENSE_REJECTED", StatusCodes.Status400BadRequest, reason)
    {
    }
}

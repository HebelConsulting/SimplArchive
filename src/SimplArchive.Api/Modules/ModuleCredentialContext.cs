using SimplArchive.ModuleAbi;

namespace SimplArchive.Api.Modules;

/// <summary>The identity a protocol request authenticated as (ABI 1.7), set by <see cref="ModuleCredentialMiddleware"/>
/// and read by the module's controllers. Scoped: one per request.</summary>
public sealed class ModuleCredentialContext : IModuleCredentialContext
{
    public ModuleCredentialIdentity? Identity { get; set; }
}

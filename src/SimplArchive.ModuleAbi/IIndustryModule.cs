using Microsoft.Extensions.DependencyInjection;

namespace SimplArchive.ModuleAbi;

/// <summary>
/// The contract an industry module implements to plug into the core (issue #836; ADR 0741). One
/// implementation per module assembly; the host discovers it at startup from the <c>Modules/</c>
/// directory, loads the assembly into its own isolated context, and calls the members below.
/// </summary>
/// <remarks>
/// <para>
/// The ABI is the ONLY surface a module sees — no Domain, no Application, no Infrastructure. Everything a
/// module contributes flows through what this interface returns and registers, and everything it may do at
/// runtime flows through the contracts its registered services receive (the facade, the fact-provider and
/// transition seams). That narrowness is deliberate: it is what lets the core refactor freely and what
/// makes a module's capabilities enumerable to a tenant administrator.
/// </para>
/// <para>
/// Lifecycle (ADRs 0740/0741): loading makes a module AVAILABLE; a per-tenant activation switches its
/// behaviour on there. Deactivation removes behaviour only — the masks a module seeded, and every document
/// filed under them, are the tenant's data and remain fully usable. A module must therefore expect its
/// subjects to have lived through a deactivation window (documents filed and edited by hand, no gates) and
/// re-derive rather than assume continuity.
/// </para>
/// </remarks>
public interface IIndustryModule
{
    /// <summary>Stable machine identity (e.g. <c>flight-school</c>) — activation records and licenses
    /// (ADR 0740) bind to this, so it must never change across versions.</summary>
    string ModuleId { get; }

    /// <summary>The human name an administrator sees in the module list.</summary>
    string DisplayName { get; }

    /// <summary>
    /// The ABI major version this module was built against. The host refuses to load a module whose major
    /// differs from its own — cleanly, with an admin-facing message, the module staying inactive and the
    /// tenant's data untouched (ADR 0741: major locks, minor floats).
    /// </summary>
    int AbiMajorVersion { get; }

    /// <summary>
    /// The masks this module contributes. Seeded into a tenant at activation, idempotently, and healed on
    /// upgrade the way the core's own well-known masks are. Permanent tenant data once seeded (ADR 0740).
    /// </summary>
    IReadOnlyList<ModuleMaskSeed> Masks { get; }

    /// <summary>
    /// Registers the module's own services — fact providers, transition handlers, controllers' collaborators.
    /// Called once at load, into the host's container; scoped/transient lifetimes behave as they do for core
    /// services. What a module registers here is its private business; what the CORE calls is only what the
    /// seam interfaces name.
    /// </summary>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Declares the module's state machines (ADR 0742): derived statuses and guarded transitions over its
    /// mask-worn subjects, in the enumerable grammar the core can evaluate AND explain. Called once at
    /// load, after <see cref="ConfigureServices"/>. Default: no machines — a module of pure masks is legal.
    /// </summary>
    void DefineStateMachines(IStateMachineDefinitions machines)
    {
    }
}

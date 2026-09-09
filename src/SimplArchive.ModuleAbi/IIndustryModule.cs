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
    /// The vendor's license verify key as a PEM <c>SubjectPublicKeyInfo</c> (ECDsa P-256). Ships inside
    /// the module — no phone-home, air-gap-friendly (ADR 0743); the core verifies a tenant's
    /// <see cref="ModuleLicense"/> against this at activation.
    /// </summary>
    string LicenseVerifyKeyPem { get; }

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

    /// <summary>
    /// The read-model contexts this module owns (ADR 0738) — wired, migrated and transaction-enlisted by
    /// the host. Default: none; a module of documents alone needs no projections.
    /// </summary>
    IReadOnlyList<ModuleReadModelSet> ReadModels => [];

    /// <summary>
    /// The rels this module contributes to the API root — its entry into the hypermedia graph
    /// (ADR 0737). Emitted only for tenants where the module is ACTIVE; for everyone else the module's
    /// surface simply does not exist (ADR 0543's absence semantics, which the host also enforces at the
    /// route: an inactive tenant's request to a module controller answers 404 <c>MODULE_NOT_ACTIVE</c>).
    /// Default: none — a module may be reachable purely through its transitions and masks.
    /// </summary>
    IReadOnlyList<ModuleRootLink> RootLinks => [];

    /// <summary>
    /// The external hosts this module's <see cref="IModuleHttpClient"/> may reach (ABI 0.6) — the allowlist
    /// the core's SSRF policy (ADR 0717) enforces on every request the module makes. A host not named here
    /// is refused before any request leaves the process, so a module's network egress is declared up front
    /// and auditable, exactly as its archive reach is enumerable through the facade. Bare host names
    /// (<c>aviationweather.gov</c>, <c>www.skybriefing.com</c>), no scheme or path. Default: none — a module
    /// that fetches nothing declares nothing, and its injected client refuses every host.
    /// </summary>
    IReadOnlyList<string> OutboundHosts => [];

    /// <summary>The per-tenant configuration this module needs (ABI 0.12, core ADR 0772): the customer's own
    /// account with an external service, an endpoint, a scheme identifier. Declared rather than free-form, so
    /// the core renders and validates the admin form and owns the secret path; read a value back through
    /// <see cref="IModuleArchiveFacade.GetSettingAsync"/>. Default: none — a module that needs no
    /// configuration declares none, and no form appears for it.</summary>
    IReadOnlyList<ModuleSetting> Settings => [];

    /// <summary>The module's localized refusal and status-diagnosis texts (ABI 0.10, core ADR 0767):
    /// culture → code → template. Refusal templates use {0}-style slots for the exception's Args; status
    /// templates use {value} for the condition's evaluated value. The host resolves the request culture
    /// (falling back culture → "en" → the composed invariant message) and, for refusals, tries
    /// "CODE.arg0" before "CODE" — the role-dependent-sentence trick. Default empty: an older module
    /// keeps today's behaviour exactly.</summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LocalizedTexts =>
        System.Collections.Immutable.ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty;
}

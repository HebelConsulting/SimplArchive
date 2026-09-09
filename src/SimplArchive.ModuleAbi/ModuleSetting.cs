namespace SimplArchive.ModuleAbi;

/// <summary>
/// A per-tenant configuration value an industry module declares (ABI 0.12, ADR 0772) — the module's own
/// account with an external service, its endpoint, its scheme identifier.
/// </summary>
/// <remarks>
/// <para>
/// Declared like <see cref="ModuleMaskSeed"/> and <c>LocalizedTexts</c> rather than free-form, so the core
/// renders and validates the admin form generically and the module ships no UI. It also means the
/// <b>secret path is written once, in the core</b>, instead of each module getting a chance to store a
/// credential in the clear.
/// </para>
/// <para>
/// The value is per TENANT, not per deployment: a module integrating a customer's own external account —
/// a school's weather subscription, a booking system, a mail relay — must not make every tenant in an
/// installation share one identity. Read it through
/// <see cref="IModuleArchiveFacade.GetSettingAsync"/>, which answers for the calling tenant and the calling
/// module only.
/// </para>
/// </remarks>
/// <param name="Key">The stable identifier this module reads the value back by ("autorouter.clientId").
/// Unique within the module; it is part of the stored row's key, so renaming one strands its value.</param>
/// <param name="Label">What the administrator sees on the form ("Autorouter client ID").</param>
/// <param name="IsSecret">Whether the value is a credential. A secret is encrypted at rest (transit
/// encryption, the same path as a TOTP secret and the audit-webhook secret) and is <b>never readable back
/// over the wire</b> — the admin surface reports only whether one is set. The module itself reads the
/// plaintext through the facade, which is the whole point of storing it.</param>
/// <param name="Description">Optional help text under the field — where to obtain the value, what it is
/// for. Keep it to a sentence; the module manual is where the procedure belongs.</param>
public sealed record ModuleSetting(
    string Key,
    string Label,
    bool IsSecret = false,
    string? Description = null);

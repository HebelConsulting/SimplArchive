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
    string? Description = null)
{
    /// <summary>
    /// What kind of value this holds, so the admin form can render the right control and the host can reject
    /// a value the setting cannot mean. Defaults to <see cref="ModuleSettingKind.Text"/>, which is what every
    /// setting declared before ABI 0.27 is.
    /// </summary>
    /// <remarks>
    /// An INIT-ONLY PROPERTY rather than a fifth primary-constructor parameter, per ADR 0789: adding a
    /// parameter changes the record's constructor, and a module compiled against an older ABI then dies with
    /// <c>MissingMethodException</c> — after loading successfully, so the version gate does not catch it. That
    /// is exactly how ABI 0.21 took the kiosk down for 1h34m (#1147).
    /// </remarks>
    public ModuleSettingKind Kind { get; init; } = ModuleSettingKind.Text;
}

/// <summary>What a <see cref="ModuleSetting"/> holds (ABI 0.27).</summary>
public enum ModuleSettingKind
{
    /// <summary>Free text — an endpoint, an account name, a credential. Every setting before ABI 0.27.</summary>
    Text = 0,

    /// <summary>
    /// A yes/no. Stored as <c>"true"</c> or <c>"false"</c>; the host renders a checkbox and refuses anything
    /// else, so a typo cannot become a third state nobody handles.
    /// </summary>
    Boolean = 1,
}

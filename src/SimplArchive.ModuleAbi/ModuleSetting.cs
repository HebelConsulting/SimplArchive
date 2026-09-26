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

    /// <summary>
    /// The permitted values when <see cref="Kind"/> is <see cref="ModuleSettingKind.Choice"/>; empty otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// INIT-ONLY, for the same reason as <see cref="Kind"/> and with the same evidence behind it (ADR 0789):
    /// a fifth primary-constructor parameter changes the record's constructor, and a module compiled against
    /// an older ABI then dies with <c>MissingMethodException</c> — <b>after loading successfully</b>, so the
    /// version gate does not catch it. That is how ABI 0.21 took the kiosk down for 1h34m (#1147).
    /// </para>
    /// <para>
    /// The values are stored and compared VERBATIM — they are the setting's vocabulary, not display text. A
    /// module that wants a translated label renders it from the value, the way every other module-supplied
    /// text works (ADR 0767); putting the label here would make the stored value depend on the reader's
    /// language.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Choices { get; init; } = [];
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

    /// <summary>
    /// One of a fixed set the module declares in <see cref="ModuleSetting.Choices"/>. The host renders a
    /// select and refuses a value outside the set — the same guarantee <see cref="Boolean"/> gives,
    /// generalised beyond two.
    /// </summary>
    /// <remarks>
    /// Reach for this when the answers are one decision rather than independent flags. The first case was
    /// <i>"may users register their own certificates, and how?"</i> — off, hardware only, or any method — and
    /// encoding it as two booleans would have let an administrator save a combination that means nothing
    /// (<i>may not enrol, but may generate</i>). A set makes the illegal states unrepresentable, which is the
    /// same reason <see cref="Boolean"/> exists rather than <see cref="Text"/> holding <c>"true"</c>.
    /// </remarks>
    Choice = 2,
}

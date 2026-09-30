namespace SimplArchive.ModuleAbi;

/// <summary>
/// A module declaring that a PERSON may enrol their own credential, and where (ABI 1.3, core #1502).
/// </summary>
/// <remarks>
/// <para>
/// <b>A module cannot contribute UI</b> — it contributes controllers and root links (ADR 0737), and the
/// clients are the core's. So a module that wants a person to be able to enrol something for themselves has,
/// until now, been able to serve the endpoint and nothing else: the surface was reachable only by a
/// hand-written request, which is how a tenant could switch self-service ON and enable something no client
/// could find.
/// </para>
/// <para>
/// This is the same kind of statement the ABI already takes for settings, localized texts and root links:
/// the module DECLARES what it needs and the core renders it. The module owns neither the dialog nor its
/// wording; it owns the route, the rule for when the route is open, and the key naming the thing being
/// enrolled.
/// </para>
/// <para>
/// <b>INIT-ONLY PROPERTIES, and that is not style.</b> Anything added to this record later must be an
/// init-only property or arrive by a secondary constructor — never a new primary-constructor parameter. A new
/// parameter changes the signature, so a module compiled against the older record calls a constructor that no
/// longer exists: ABI 0.21 did exactly that to <c>ModuleMaskSeed</c> and crash-looped the kiosk for 1h34m
/// from a static initializer, with nothing in the version gate able to see it (see
/// <see cref="ModuleAbiVersion"/>). The two parameters below are the ones this cannot work without; the rest
/// are properties on purpose.
/// </para>
/// </remarks>
/// <param name="Route">
/// Where the credential is POSTed — the module's own endpoint, as it routes it. The core does not compose
/// this; it is the module's statement of its own address, the same as a <see cref="ModuleRootLink"/>'s path.
/// </param>
/// <param name="GateSetting">
/// The module setting that decides whether the surface is open, by key. The core reads it per tenant and
/// offers nothing when it is off — because an affordance that is present and refuses is the lying affordance
/// ADR 0543 exists to prevent, and "off" is exactly "not available to you, here, now".
/// </param>
public sealed record PerUserEnrolment(string Route, string GateSetting)
{
    /// <summary>
    /// The localized text key for what is being enrolled — "Reader certificate", say.
    /// </summary>
    /// <remarks>
    /// A KEY rather than a string, resolved through the module's own <c>LocalizedTexts</c> (ABI 0.10): the
    /// wording is the module's to translate and the core's to render, and a literal here would appear in one
    /// language on a surface that has four.
    /// </remarks>
    public string TitleKey { get; init; } = string.Empty;

    /// <summary>
    /// What the person is asked to supply, so the core renders the right control.
    /// </summary>
    /// <remarks>
    /// Deliberately a small closed set rather than a free-form hint: the core has to know whether to offer a
    /// file picker, a text area, or both, and a module inventing its own words for that would be a
    /// declaration the core cannot act on — the failure ADR 0835 describes for a setting whose value the
    /// module can read and the core cannot.
    /// </remarks>
    public PerUserEnrolmentInput Input { get; init; } = PerUserEnrolmentInput.Certificate;

    /// <summary>
    /// The field name the credential is sent as, inside the POSTed object.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed, because the module owns its own request shape and the core is filling in a
    /// form on its behalf. Defaulted to the one the first module uses, so a module whose endpoint already
    /// takes <c>certificatePem</c> declares nothing extra.
    /// </remarks>
    public string Field { get; init; } = "certificatePem";

    /// <summary>
    /// The field a human-readable label is sent as, or empty when the endpoint takes none.
    /// </summary>
    public string LabelField { get; init; } = "label";
}

/// <summary>What a person supplies when enrolling — what control the core offers (ABI 1.3).</summary>
public enum PerUserEnrolmentInput
{
    /// <summary>
    /// An X.509 certificate: a file (PEM or DER) or pasted PEM text.
    /// </summary>
    /// <remarks>
    /// The core refuses anything containing a PRIVATE KEY before sending it, whatever the module's own policy
    /// says — the useful moment to say "that is not what you want to hand over" is while it is still in front
    /// of the person who chose it, and the server refusing it later is a worse place to learn.
    /// </remarks>
    Certificate = 0,
}

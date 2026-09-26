using Microsoft.Extensions.Configuration;

namespace SimplArchive.Infrastructure.Encryption;

/// <summary>What encryption a tenant gets. One map, read in one place.</summary>
public enum EncryptionMode
{
    /// <summary>Nothing. Content is stored as the object storage received it.</summary>
    None = 0,

    /// <summary>
    /// The storage-compromise tier that ships today (ADRs 0813/0818): content envelope-encrypted at rest under
    /// the installation's HSM-held KEK, and IMAP messages enveloped to the recipient's certificate on fetch.
    /// Search, previews and every ordinary door keep working.
    /// </summary>
    Storage = 1,

    /// <summary>
    /// The strict tier (ADR 0825, epic #1351): everything <see cref="Storage"/> gives, plus the core serving no
    /// plaintext — every read is a CMS envelope to the reader's card certificate, search is metadata-only, and
    /// a client that cannot decrypt is refused rather than quietly served plaintext.
    /// </summary>
    Strict = 2,
}

/// <summary>
/// Which encryption mode each tenant is in — the ONE place the answer is read.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a map rather than one list per tier.</b> The first shape was two lists, <c>Encryption:Tenants</c> and
/// <c>Encryption:StrictTenants</c>, and they looked identical while behaving oppositely on "empty": the first
/// meant EVERY tenant (ADR 0813's contract, so an installation setting only the service URL kept
/// per-installation behaviour) and the second meant NO tenant (because switching off search, previews and every
/// plaintext door for everybody was not a safe default). Each default was individually right for its own tier —
/// narrow an open gate, versus open a closed one — which is exactly why the pair was dangerous: a reader infers
/// the second from the first and is wrong, and nothing tells them.
/// </para>
/// <para>
/// One map cannot be asymmetric with itself. It also retires a check the two lists needed: a tenant could be
/// listed as strict while absent from the at-rest list, advertising the stronger tier over plaintext storage,
/// and that had to be caught at startup. Here <see cref="EncryptionMode.Strict"/> IS at-rest-plus-envelope, so
/// the incoherent state cannot be written down.
/// </para>
/// <para>
/// <b>A DEFAULT plus per-tenant overrides, rather than a wildcard key.</b> The first attempt used <c>"*"</c>
/// inside the map, and it does not survive writing the configuration down: <c>*</c> is not a legal environment
/// variable name, and both the development stack and the kiosk configure through compose environment entries.
/// A dynamic tenant name as a key fails the same way. So the installation-wide answer is its own setting,
/// <c>Encryption:DefaultMode</c>, and <c>Encryption:Modes:&lt;tenant&gt;</c> overrides it.
/// </para>
/// <para>
/// The default exists because the alternative points the wrong way. Under the old list semantics a newly
/// provisioned tenant was automatically encrypted; with per-tenant entries alone it would be automatically
/// NOT — so an installation wanting everything covered would have to remember to edit configuration for each
/// new tenant, and the cost of forgetting is a silently unencrypted one.
/// </para>
/// <para>
/// <b>Read per call</b>, so the map is editable without a restart (ADR 0813) — the tier is a per-tenant
/// commercial decision rather than a deployment shape.
/// </para>
/// </remarks>
public sealed class EncryptionModes(IConfiguration configuration)
{
    /// <summary>Per-tenant overrides: <c>Encryption:Modes:&lt;tenant name&gt;</c>.</summary>
    public const string Section = "Encryption:Modes";

    /// <summary>The mode every tenant gets unless <see cref="Section"/> names it.</summary>
    public const string DefaultSection = "Encryption:DefaultMode";

    /// <summary>Retired keys, refused at startup rather than silently ignored.</summary>
    public static readonly string[] LegacySections = ["Encryption:Tenants", "Encryption:StrictTenants"];

    /// <summary>The encryption service's address. Null means nothing performs encryption at all.</summary>
    public const string ServiceUrlKey = "Encryption:ServiceUrl";

    /// <summary>This tenant's mode — its own override, else the installation default, else none.</summary>
    public EncryptionMode ModeFor(string tenantName) =>
        Map().TryGetValue(tenantName, out var mode) ? mode : Default();

    private EncryptionMode Default() => Parse(configuration[DefaultSection], DefaultSection);

    /// <summary>True when content is encrypted at rest for this tenant — both tiers do.</summary>
    public bool Applies(string tenantName) => ModeFor(tenantName) >= EncryptionMode.Storage;

    /// <summary>True when the strict tier applies.</summary>
    public bool IsStrict(string tenantName) => ModeFor(tenantName) == EncryptionMode.Strict;

    /// <summary>
    /// Refuses to start while a retired key is still present, naming what to write instead.
    /// </summary>
    /// <remarks>
    /// Silently ignoring the old keys is the one option that must not be taken: an installation that lists
    /// tenants under <c>Encryption:Tenants</c> today would keep running with encryption simply switched OFF,
    /// which is the failure nobody notices — every surface behaves correctly and the guarantee is absent. So
    /// the old config is a startup refusal that translates itself, not a compatibility shim.
    ///
    /// Not part of <c>ProductionReadinessValidator</c>: that runs outside Development only, because it is about
    /// dev-grade settings that must not reach production. This is a configuration that is wrong everywhere.
    /// </remarks>
    public static void ThrowIfLegacyConfigured(IConfiguration configuration)
    {
        var present = LegacySections
            .Where(section => configuration.GetSection(section).GetChildren().Any())
            .ToList();

        if (present.Count == 0)
        {
            return;
        }

        var names = configuration.GetSection(LegacySections[0]).GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        var suggestion = names.Count > 0
            ? $"    {Section.Replace(':', '/')}:\n" + string.Join("\n", names.Select(name => $"      {name}: Storage"))
            : $"    {DefaultSection.Replace(':', '/')}: Storage";

        throw new InvalidOperationException(
            $"{string.Join(" and ", present)} {(present.Count == 1 ? "is" : "are")} retired (ADR 0825). "
            + $"Encryption is now a mode per tenant — {DefaultSection} for the installation, {Section}:<tenant> "
            + "to override it — so the two tiers cannot disagree about what an empty list means. Write "
            + $"instead:\n\n{suggestion}\n\n"
            + $"Modes are {string.Join(", ", Enum.GetNames<EncryptionMode>())}. Setting {DefaultSection} keeps "
            + "the old \"no list means every tenant\" behaviour, which also keeps newly provisioned tenants "
            + "encrypted.");
    }

    /// <summary>
    /// Refuses every configuration that claims encryption the installation will not perform.
    /// </summary>
    /// <remarks>
    /// One call site, two rules, because they are the same failure wearing different clothes: a retired key
    /// silently means "off", and a mode with no service silently means "off". Neither reports anything, and
    /// both leave an installation believing it has a tier it does not have.
    /// </remarks>
    public static void ThrowIfMisconfigured(IConfiguration configuration)
    {
        ThrowIfLegacyConfigured(configuration);
        ThrowIfModeHasNoService(configuration);
    }

    /// <summary>
    /// Refuses to start when a mode claims encryption the installation has no service to perform (#1406).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What went wrong.</b> A tenant set to <c>Strict</c> with no <c>Encryption:ServiceUrl</c> served
    /// <b>plaintext to an anonymous caller</b> — measured: a version advertised a presigned object-storage URL
    /// and fetching it with no credentials returned <c>BEGIN:VCALENDAR</c>. The two halves of the tier are
    /// controlled by DIFFERENT settings: this class reads only the mode map, while the refusal to hand out
    /// readable bytes lives in <c>EncryptingObjectStorageClient</c>, which is registered only when the service
    /// URL is set. So the mode said one thing and the doors did another, and nothing anywhere said so.
    /// </para>
    /// <para>
    /// <b>Why a refusal rather than a warning.</b> An administrator has written <c>Strict</c> in their
    /// configuration, every surface behaves normally, and the guarantee is simply absent — with nothing in a
    /// log, a health check or a startup line to say so. That is the same failure the retired
    /// <see cref="LegacySections"/> keys are refused for, and the argument recorded there applies unchanged:
    /// an installation running with encryption silently off is the failure nobody notices.
    /// </para>
    /// <para>
    /// <b>In Development too</b>, deliberately, unlike <c>ProductionReadinessValidator</c> — which exists to
    /// keep dev-grade settings out of production. This is not a setting that is fine locally and wrong in
    /// production: it is a configuration that lies everywhere, and it was met on a developer's machine
    /// (a live demonstration served plaintext from a tenant configured Strict). A developer misled by it is
    /// exactly as misled as an administrator.
    /// </para>
    /// <para>
    /// The cost is accepted and worth naming: configuring a mode without the service used to be a convenient
    /// way to demonstrate envelope DELIVERY without running the encryption service, and that stops working.
    /// A delivery-without-at-rest tier is a real thing people want, and it is being built as its own named
    /// mode rather than left as a misconfiguration that happens to work (#1411).
    /// </para>
    /// </remarks>
    public static void ThrowIfModeHasNoService(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(configuration[ServiceUrlKey]))
        {
            return;
        }

        var claimed = new List<string>();

        if (Parse(configuration[DefaultSection], DefaultSection) > EncryptionMode.None)
        {
            claimed.Add($"{DefaultSection} = {configuration[DefaultSection]}");
        }

        claimed.AddRange(configuration.GetSection(Section).GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value)
                && Parse(child.Value, $"{Section}:{child.Key}") > EncryptionMode.None)
            .Select(child => $"{Section}:{child.Key} = {child.Value}"));

        if (claimed.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{string.Join(", ", claimed)} — but {ServiceUrlKey} is not set, so nothing performs that "
            + "encryption (#1406, ADR 0825).\n\n"
            + "This is refused rather than ignored because it is invisible: content would be stored and "
            + "SERVED as plaintext while the configuration claims a tier, and no surface would report it.\n\n"
            + $"Either set {ServiceUrlKey} to the encryption service, or set the mode to None.");
    }

    private Dictionary<string, EncryptionMode> Map() =>
        configuration.GetSection(Section).GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value))
            .ToDictionary(child => child.Key, child => Parse(child.Value, $"{Section}:{child.Key}"),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>A configured mode name, or None when unset — an unrecognised value is refused, never ignored.</summary>
    private static EncryptionMode Parse(string? value, string where) => value switch
    {
        null or "" => EncryptionMode.None,
        _ when Enum.TryParse<EncryptionMode>(value, ignoreCase: true, out var mode) => mode,
        _ => throw new InvalidOperationException(
            $"{where} is '{value}', which is not a mode. Use one of {string.Join(", ", Enum.GetNames<EncryptionMode>())}."),
    };
}

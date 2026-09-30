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

    /// <summary>
    /// Delivery without at-rest (ADR 0834, #1411): content is stored <b>unwrapped</b>, and every read is
    /// delivered as a CMS envelope addressed to the reader's certificates. The ordinary doors keep serving.
    /// </summary>
    /// <remarks>
    /// The posture is part of the VALUE rather than a second setting (owner, 2026-09-28): a tenant's guarantee
    /// must not be assemblable from two keys that can be edited apart, because a half-configured tenant looks
    /// exactly like a configured one. Two members cost two lines; a pair of keys costs a class of silent
    /// misconfiguration, and this file already carries the scar of one (see the two retired lists above).
    /// </remarks>
    SealedDeliveryPermissive = 3,

    /// <summary>
    /// <see cref="SealedDeliveryPermissive"/> with the doors shut: every other content door refuses rather than
    /// serving readable bytes (ADR 0829), and search falls back to metadata-only — the same posture
    /// <see cref="Strict"/> has, over storage that is not wrapped.
    /// </summary>
    SealedDeliveryStrict = 4,
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

    /// <summary>True when content is wrapped in object storage for this tenant.</summary>
    /// <remarks>
    /// <para>
    /// <b>Membership, not ordering.</b> This read <c>&gt;= Storage</c>, which was true of three values and is a
    /// trap for the fourth: the modes are not a severity scale. <c>Storage</c> names what is protected,
    /// <c>Strict</c> names a posture, and the delivery-only tiers are *stronger* on delivery while wrapping
    /// <b>nothing</b> at rest — so wherever such a value were inserted, one comparison would be wrong, and
    /// wrong silently: above <c>Storage</c> it would turn at-rest wrapping ON for modes whose whole definition
    /// is that content is not wrapped.
    /// </para>
    /// <para>
    /// <c>Only_the_modes_that_wrap_at_rest_apply</c> enumerates the enum, so a new value is simply not included
    /// until somebody decides it should be.
    /// </para>
    /// </remarks>
    public bool WrapsAtRest(string tenantName) =>
        ModeFor(tenantName) is EncryptionMode.Storage or EncryptionMode.Strict;

    /// <summary>
    /// True when no door may serve readable bytes for this tenant — previews, ranges, presigned URLs, search
    /// snippets (ADRs 0825/0829).
    /// </summary>
    /// <remarks>
    /// <b>Search follows this, not the mode</b> (owner, 2026-09-28). A snippet IS plaintext served through a
    /// door, so a tenant that refuses plaintext refuses it everywhere — one sentence covering search, previews
    /// and links. The consequence is that <see cref="EncryptionMode.SealedDeliveryPermissive"/> keeps full-text
    /// search: its content is not wrapped at rest, so an index exposes nothing the bucket does not already
    /// hold, and withdrawing search there would cost a capability for no gain.
    /// </remarks>
    public bool RefusesPlaintextDoors(string tenantName) =>
        ModeFor(tenantName) is EncryptionMode.Strict or EncryptionMode.SealedDeliveryStrict;

    /// <summary>True when a content read is delivered as an envelope addressed to the reader.</summary>
    /// <remarks>
    /// The third question, and the one that separates the delivery tiers from <see cref="EncryptionMode.Storage"/>:
    /// at-rest wrapping is invisible to a reader, enveloping is the whole of what they see.
    /// </remarks>
    public bool DeliversEnvelopes(string tenantName) =>
        ModeFor(tenantName) is EncryptionMode.Strict
            or EncryptionMode.SealedDeliveryStrict or EncryptionMode.SealedDeliveryPermissive;

    /// <summary>True when IMAP serves messages enveloped to the recipient's certificate (ADR 0813).</summary>
    /// <remarks>
    /// <b>This list is deliberately the AT-REST one, and that is wrong on purpose.</b> Enveloping mail is
    /// delivery, so by ADR 0834 it belongs with the other delivery questions above — but it ships today gated
    /// on the at-rest tiers, and moving it is a change to behaviour real installations use, tracked separately
    /// as #1414 so it is not absorbed into a refactor. Naming it apart from <see cref="WrapsAtRest"/> is what
    /// makes the discrepancy visible: the two happen to hold the same modes today and mean different things,
    /// and a single predicate would have hidden that they are ever meant to differ.
    /// </remarks>
    public bool EnvelopesMail(string tenantName) =>
        ModeFor(tenantName) is EncryptionMode.Storage or EncryptionMode.Strict;

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
    /// <para>
    /// One call site for the rules answerable from CONFIGURATION alone, because they are the same failure
    /// wearing different clothes: a retired key silently means "off", and a mode with no service silently
    /// means "off". Neither reports anything, and both leave an installation believing it has a tier it does
    /// not have.
    /// </para>
    /// <para>
    /// <b>The delivery-tier rule used to be here and is not any more</b>, and the reason is worth stating
    /// because this comment previously claimed one call site was deliberate. That rule now needs a fact
    /// configuration cannot supply — whether a LOADED module answers
    /// <see cref="ModuleAbi.IIndustryModule.ReaderCertificates"/> — and modules are loaded long after this
    /// runs (`Program.cs` line ~66 versus ~400). Keeping it here meant it could only ever refuse, which is
    /// what it did: it said the module "is not available yet" after the module existed. So
    /// <see cref="ThrowIfSealedDeliveryHasNoModule"/> is called separately, once the answer exists.
    /// </para>
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

        // MEMBERSHIP, for the reason WrapsAtRest gives: this read `> None`, and the delivery tiers are
        // above None while needing no service at all — so an ordering here would demand an encryption
        // service from exactly the modes the split exists to free from one (ADR 0834).
        if (NeedsService(Parse(configuration[DefaultSection], DefaultSection)))
        {
            claimed.Add($"{DefaultSection} = {configuration[DefaultSection]}");
        }

        claimed.AddRange(configuration.GetSection(Section).GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value)
                && NeedsService(Parse(child.Value, $"{Section}:{child.Key}")))
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

    /// <summary>Modes that cannot be performed without the encryption service: the ones that wrap at rest.</summary>
    private static bool NeedsService(EncryptionMode mode) =>
        mode is EncryptionMode.Storage or EncryptionMode.Strict;

    /// <summary>Modes whose delivery is performed by the Encryption Module (ADR 0834).</summary>
    private static bool NeedsModule(EncryptionMode mode) =>
        mode is EncryptionMode.SealedDeliveryPermissive or EncryptionMode.SealedDeliveryStrict;

    /// <summary>
    /// Refuses a delivery-only mode while nothing can perform its delivery (#1411, ADR 0834).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delivery tiers envelope every read to the reader's certificates, and the component that answers
    /// <i>which certificates is this user addressed by?</i> is the <b>Encryption Module</b>, which does not
    /// exist yet (owner, 2026-09-28: the mode waits for it rather than shipping an interim source that would
    /// have to be retired). Until it does, the values are written down so the three questions above could be
    /// answered for them — and refused, so nobody configures a tier nothing performs.
    /// </para>
    /// <para>
    /// <b>The same failure as #1406, caught the same way.</b> A mode with nothing behind it does not misbehave:
    /// every surface works and the guarantee is simply absent. That is why this is a refusal rather than a
    /// warning, in Development too — a developer misled by it is exactly as misled as an administrator.
    /// </para>
    /// </remarks>
    public static void ThrowIfSealedDeliveryHasNoModule(
        IConfiguration configuration, IEnumerable<ModuleAbi.IIndustryModule> modules)
    {
        // The question is about a LOADED module, which is why this no longer rides in
        // ThrowIfMisconfigured: an installation carrying a module that answers the capability can perform
        // these tiers, and one carrying none cannot, and configuration alone cannot tell them apart.
        if (modules.Any(module => module.ReaderCertificates is not null))
        {
            return;
        }

        var claimed = new List<string>();

        if (NeedsModule(Parse(configuration[DefaultSection], DefaultSection)))
        {
            claimed.Add($"{DefaultSection} = {configuration[DefaultSection]}");
        }

        claimed.AddRange(configuration.GetSection(Section).GetChildren()
            .Where(child => !string.IsNullOrWhiteSpace(child.Value)
                && NeedsModule(Parse(child.Value, $"{Section}:{child.Key}")))
            .Select(child => $"{Section}:{child.Key} = {child.Value}"));

        if (claimed.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{string.Join(", ", claimed)} — but the delivery tiers are performed by the Encryption Module "
            + "(ADR 0834), and no module on this installation answers the reader-certificate capability, so "
            + "nothing would envelope those reads (#1411).\n\n"
            + "This is refused rather than ignored because it is invisible: content would be stored and SERVED "
            + "as plaintext while the configuration claims envelope delivery, and no surface would report it.\n\n"
            + "Either mount the Encryption Module (it declares ReaderCertificates), or use "
            + $"{nameof(EncryptionMode.Storage)} or {nameof(EncryptionMode.Strict)}.");
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

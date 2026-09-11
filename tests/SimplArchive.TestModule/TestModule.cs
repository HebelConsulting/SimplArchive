using Microsoft.Extensions.DependencyInjection;
using SimplArchive.ModuleAbi;

namespace SimplArchive.TestModule;

/// <summary>
/// The loader/seam/engine fixture: the smallest complete module — two masks, one machine, one fact
/// provider, one service. Its machine is deliberately the epic's hardest small shape (the medical): an
/// expiry, a temporarily-void flag that overrides valid dates, and an aggregate fact.
/// </summary>
public sealed class TestModule : IIndustryModule
{
    public static readonly Guid DossierMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000000");
    public static readonly Guid CertificateMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000001");
    public static readonly Guid EntryMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000002");

    public string ModuleId => "test-module";

    public string DisplayName => "Test Module";

    public int AbiMajorVersion => ModuleAbiVersion.Major;

    /// <summary>Settable by tests (the <see cref="TestFactProvider.Landings"/> idiom): licensing tests
    /// generate a key pair at runtime and plant the public half here — no key material in the repo.</summary>
    public static string VerifyKeyPem { get; set; } = string.Empty;

    /// <summary>Settable by the escalation test: who the "Expiring" escalation names as its recipient (the
    /// module resolves the audience; the core resolves the e-mail to a tenant user).</summary>
    public static string EscalationRecipientEmail { get; set; } = string.Empty;

    /// <summary>
    /// The static works only when the test constructs the module in ITS OWN load context; a module the
    /// LOADER brought in lives in an isolated context whose statics the test cannot reach (ADR 0741's
    /// isolation working as designed). The E2E activation circle therefore plants the key in an
    /// environment variable, which crosses contexts because there is only one process environment.
    /// </summary>
    /// <summary>The localization seam under test (ABI 0.10, ADR 0767): one German entry proves the
    /// culture path; every uncovered code proves the fallback-to-composed-English path.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LocalizedTexts { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["de"] = new Dictionary<string, string>
            {
                ["test.certificate-expired"] = "Das Zertifikat ist am {value} abgelaufen.",
                ["TEST_HANDLER_REFUSED"] = "Der Testschritt wurde abgelehnt.",
            },
        };

    public string LicenseVerifyKeyPem =>
        Environment.GetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_VERIFY_KEY") ?? VerifyKeyPem;

    /// <summary>
    /// The booking-admission seam under test (ABI 0.15, ADR 0781). Driven by an ENVIRONMENT VARIABLE for the
    /// same reason the verify key is: the loader gives a module its own load context, so a static this module
    /// reads is not the static a test in the host context writes. One process environment crosses both.
    /// <list type="bullet">
    /// <item><c>refuse</c> — the module's own deliberate refusal, carrying the claim set it was shown so a
    /// test can assert the facts arrived (every claim, not just the holding one).</item>
    /// <item><c>throw</c> — a BROKEN handler, which the core must turn into a vetting failure rather than
    /// into an admitted booking.</item>
    /// <item>anything else, including unset — admit, which is what every other booking test relies on.</item>
    /// </list>
    /// Always non-null, so the seam is exercised on every booking written in a tenant where this module is
    /// active: a hook that only existed when a test asked for it would leave the wiring itself untested.
    /// </summary>
    public Func<BookingAdmissionContext, Task>? ReviewBooking => async context =>
    {
        switch (Environment.GetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_BOOKING_REVIEW"))
        {
            case "refuse":
                throw new ModuleApiException(
                    "TEST_BOOKING_REFUSED",
                    409,
                    $"The test module refused this booking ({Describe(context.Request)}).",
                    [Describe(context.Request)]);

            case "throw":
                throw new InvalidOperationException("The test module's booking handler is broken.");

            // Reports whether the module can READ a document named in an environment variable — the probe
            // behind "the review runs as the module, not as the writer" (core ADR 0781). Pointed at a
            // document only the module's principal is granted on, a refusal saying seen=True proves the
            // reads behind a vetting rule do not depend on who is writing the booking.
            case "probe":
                var target = Guid.Parse(Environment.GetEnvironmentVariable("SIMPLARCHIVE_TESTMODULE_PROBE_DOCUMENT")!);
                var seen = await context.Archive.GetDocumentAsync(target) is not null;
                throw new ModuleApiException("TEST_BOOKING_PROBE", 409, $"seen={seen}", [$"seen={seen}"]);

            default:
                return;
        }
    };

    /// <summary>What the module was shown, rendered so a test can assert it over the wire: how many claims,
    /// how many of them stand for a person, and whether this is a first booking or a rebooking.</summary>
    private static string Describe(BookingAdmissionRequest request) =>
        $"claims={request.Claims.Count}"
        + $" persons={request.Claims.Count(c => c.RepresentsUserId is not null)}"
        + $" holding={request.Claims.Count(c => c.IsHolding)}"
        + $" isNew={request.IsNew}"
        + $" newClaims={request.Claims.Count(c => c.IsNewClaim)}"
        + $" dropped={request.Claims.Count(c => c.IsDropped)}"
        + $" slotChanged={request.SlotChanged}";

    public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
    [
        new ModuleMaskSeed(DossierMaskId, "Test Dossier", IsFolderMask: true, IsBookable: false,
        [
            // The escalation's own idempotency marker (ABI 0.5): the handler stores the certificate date it
            // warned about, so it warns once and re-arms only when that date moves.
            new ModuleFieldSeed("Reminder sent for", "Text", IsRequired: false),
            // The proposal fixture's target (ABI 0.11, ADR 0769): the field the "mentors" proposal fills.
            new ModuleFieldSeed("Mentor", "Text", IsRequired: false),
        ]),
        new ModuleMaskSeed(CertificateMaskId, "Test Certificate", IsFolderMask: false, IsBookable: false,
        [
            new ModuleFieldSeed("Valid to", "Date", IsRequired: false),
            new ModuleFieldSeed("Temporarily void", "Boolean", IsRequired: false),
            // Text on purpose (ABI 0.2, #1014): the one type where a saved-blank value can exist, which is
            // what the Present test's whitespace case needs (the core refuses a blank Date outright).
            new ModuleFieldSeed("Issuer", "Text", IsRequired: false),
        ]),
        // Its own mask, learned the hard way: entries wearing the certificate mask became the NEWEST
        // certificate, and the first logged entry shadowed the medical — every later act read "no Valid
        // to" and refused. A log entry is not a certificate; the model must say so.
        new ModuleMaskSeed(EntryMaskId, "Test Entry", IsFolderMask: false, IsBookable: false,
        [
            // The list-field fixture (ABI 0.2, #1014): what SetFieldListAsync round-trips against.
            new ModuleFieldSeed("Tags", "Text", IsList: true),
        ]),
    ];

    public IReadOnlyList<ModuleRootLink> RootLinks { get; } =
    [
        // The module's entry into the hypermedia graph (ADR 0737): module-prefixed rel, module-private path.
        new ModuleRootLink("test-module:status", "/api/test-module/status", "GET"),
    ];

    public IReadOnlyList<ModuleReadModelSet> ReadModels { get; } = [new ModuleReadModelSet(typeof(TestReadModelContext))];

    /// <summary>Per-tenant configuration (ABI 0.12, ADR 0772) — one plain value and one secret, which are
    /// the two paths worth proving: what the admin surface may echo back, and what it must never.</summary>
    public IReadOnlyList<ModuleSetting> Settings { get; } =
    [
        new ModuleSetting("endpoint", "Service endpoint", Description: "Where the fixture would call."),
        new ModuleSetting("apiSecret", "API secret", IsSecret: true),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TestModuleMarker>();
        // SCOPED, both: they read the module's read-model context, which lives per request (ADR 0738).
        services.AddScoped<IModuleFactProvider, TestFactProvider>();
        services.AddScoped<IModuleProjectionRebuilder, TestLandingRebuilder>();
    }

    public void DefineStateMachines(IStateMachineDefinitions machines) =>
        machines.Machine("test-pilot", DossierMaskId)
            .Status("MayAct",
                StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DateNotPast, null,
                    "test.certificate-expired", "The certificate expired {value}."),
                // The epic's temporarily-void shape: a certificate can be suspended while its dates still
                // read valid — NotEquals holds for an absent flag (a checkbox never ticked is not "true").
                StateCondition.ChildField(CertificateMaskId, "Temporarily void", ConditionTest.NotEquals, "true",
                    "test.certificate-void", "The certificate is temporarily void."),
                StateCondition.FactAtLeast("testLandings", 3,
                    "test.recency", "{value} recent landings; 3 required."))
            // The presence primitive (ABI 0.2, #1014): satisfied only by a non-blank value — the shape
            // the flight-school's "names a pilot" gates need.
            .Status("Dated",
                StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.Present, null,
                    "test.no-expiry-date", "The certificate carries no expiry date."))
            .Status("Attributed",
                StateCondition.ChildField(CertificateMaskId, "Issuer", ConditionTest.Present, null,
                    "test.no-issuer", "The certificate names no issuer."))
            // The date-window primitives (ABI 0.4, flight-school #3): Expired is the clean inverse of MayAct's
            // DateNotPast; Expiring narrows the future to the approaching 30-day edge.
            .Status("Expired",
                StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DatePast, null,
                    "test.not-expired", "The certificate has not expired ({value})."))
            .Status("Expiring",
                StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DateWithinDays, "30",
                    "test.not-expiring", "The certificate is not within 30 days of expiry ({value})."))
            // The escalation off that status (ABI 0.5): while a dossier's newest certificate is expiring, warn
            // the configured recipient ONCE — the marker stores the date warned about, so a re-run sends nothing
            // and a moved date re-arms. The module resolves who-and-what; the core resolves e-mail→user + delivers.
            .Escalates("Expiring", async ctx =>
            {
                var certs = await ctx.Archive.GetChildrenAsync(ctx.SubjectDocumentId, CertificateMaskId);
                var deadline = certs.Count > 0 && certs[^1].Fields.TryGetValue("Valid to", out var v) ? v : string.Empty;

                var subject = await ctx.Archive.GetDocumentAsync(ctx.SubjectDocumentId);
                var warnedFor = subject is not null && subject.Fields.TryGetValue("Reminder sent for", out var w) ? w : string.Empty;
                if (warnedFor == deadline)
                {
                    return []; // already warned for this deadline
                }

                await ctx.Archive.SetFieldsAsync(ctx.SubjectDocumentId, new Dictionary<string, string> { ["Reminder sent for"] = deadline });
                return [new EscalationNotice(EscalationRecipientEmail, "Certificate expiring", $"The certificate expires {deadline}.")];
            })
            .Transition("log-entry", "Log entry",
                [
                    StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DateNotPast, null,
                        "test.certificate-expired", "The certificate expired {value}."),
                ],
                async context =>
                {
                    // The document write AND the projection write, one act (ADRs 0737/0738): the engine's
                    // transaction covers both, which is what the fact provider's answer rests on.
                    await context.Archive.CreateDocumentAsync(
                        context.SubjectDocumentId, EntryMaskId, $"Entry {Guid.NewGuid():N}");
                    await IncrementAsync(context, 1);
                })
            // The fact-gated act (ADR 0736 over the wire): allowed only once the counter the module
            // maintains crosses the threshold — the read model answering a gate.
            .Transition("certify", "Certify",
                [StateCondition.FactAtLeast("testLandings", 3, "test.recency", "{value} recent landings; 3 required.")],
                _ => Task.CompletedTask)
            // The proposal fixture (ABI 0.11, ADR 0769): every OTHER dossier is a proposable mentor —
            // deterministic, and the self-exclusion is the one rule worth pinning over the wire.
            .Proposal("mentors", "Propose mentor", "Mentor", async context =>
            {
                var dossiers = await context.Archive.GetByMaskAsync(DossierMaskId);
                return [.. dossiers
                    .Where(d => d.Id != context.SubjectDocumentId)
                    .Select(d => new ProposalItem(d.Name, d.Name, "a test mentor"))];
            })
            // The handler-thrown refusal fixture (ADR 0767's OTHER path): a ModuleApiException from inside
            // a handler reaches ApiExceptionHandler where the ambient culture has already unwound — the
            // catalog must still resolve for the REQUEST culture (the bug found live 2026-09-08).
            .Transition("refuse", "Refuse", [],
                _ => throw new SimplArchive.ModuleAbi.ModuleApiException(
                    "TEST_HANDLER_REFUSED", 409, "The test step was refused."))
            // The rollback fixture (ADR 0737): a handler that WRITES and then throws — what it wrote must
            // never be seen, because the engine owns the transaction and a throw rolls the act back.
            .Transition("explode", "Explode", [],
                async context =>
                {
                    await context.Archive.CreateDocumentAsync(
                        context.SubjectDocumentId, EntryMaskId, $"Never {Guid.NewGuid():N}");
                    await IncrementAsync(context, 1); // the projection write must roll back with the document
                    throw new InvalidOperationException("The handler failed after writing.");
                })
            // The populate-on-open hook (ABI 0.6): opening the subject STAGES a content-bearing ephemeral
            // document under it — the DABS/METAR shape (a real module would fetch the bytes; here a fixed
            // payload). Replaced in place on every open, so the folder never accumulates a second entry.
            .AutoRefreshOnOpen("refresh", "Refresh", async context =>
            {
                var existing = await context.Archive.GetChildrenAsync(context.SubjectDocumentId, EntryMaskId);
                var replaceId = existing.Count > 0 ? (Guid?)existing[0].Id : null;
                await context.Archive.StageContentAsync(
                    context.SubjectDocumentId, EntryMaskId, "Staged entry",
                    System.Text.Encoding.UTF8.GetBytes("staged content"), "txt",
                    DateTimeOffset.UtcNow.AddHours(1), replaceDocumentId: replaceId);
            });

    private static async Task IncrementAsync(TransitionContext context, int by)
    {
        var db = (TestReadModelContext)context.Services.GetService(typeof(TestReadModelContext))!;
        var row = await db.LandingCounters.FindAsync(context.SubjectDocumentId);
        if (row is null)
        {
            row = new TestLandingCounter { DossierId = context.SubjectDocumentId };
            db.LandingCounters.Add(row);
        }

        row.Count += by;
        await db.SaveChangesAsync();
    }
}

/// <summary>Registered by <see cref="TestModule.ConfigureServices"/> so a test can see the call happened.</summary>
public sealed class TestModuleMarker;

/// <summary>
/// The aggregate family made REAL (ADR 0738): the fact is the module's own read model — the counter the
/// log-entry transition maintains in its transaction — never a computed-per-ask aggregate over EAV rows.
/// An absent row is zero: a dossier nobody logged against has no landings, and a wiped projection reads
/// as zero until its rebuild re-derives it.
/// </summary>
public sealed class TestFactProvider(TestReadModelContext db) : IModuleFactProvider
{
    public IReadOnlyList<string> FactNames { get; } = ["testLandings"];

    public async Task<FactValue> GetAsync(string factName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default)
    {
        var count = (await db.LandingCounters.FindAsync([subjectDocumentId], cancellationToken))?.Count ?? 0;
        return new FactValue(count.ToString(), $"{count} landings in the window");
    }
}

/// <summary>The rebuild contract, honored (ADR 0738): the counter re-derived from the entry documents.</summary>
public sealed class TestLandingRebuilder(TestReadModelContext db, IModuleArchiveFacade archive) : IModuleProjectionRebuilder
{
    public IReadOnlyList<string> ProjectionNames { get; } = ["landings"];

    public async Task RebuildAsync(string projectionName, CancellationToken cancellationToken = default)
    {
        foreach (var dossier in await archive.GetByMaskAsync(TestModule.DossierMaskId, cancellationToken))
        {
            var entries = (await archive.GetChildrenAsync(dossier.Id, TestModule.EntryMaskId, cancellationToken)).Count;
            var row = await db.LandingCounters.FindAsync([dossier.Id], cancellationToken);
            if (row is null)
            {
                row = new TestLandingCounter { DossierId = dossier.Id };
                db.LandingCounters.Add(row);
            }

            row.Count = entries;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Acl;
using SimplArchive.Infrastructure.Conversion;
using SimplArchive.Infrastructure.Masks;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Search;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers SimplArchiveDbContext against PostgreSQL, reading the connection string from the
    /// "Default" connection string entry (see ADR: Configuration / secrets management strategy —
    /// standard ASP.NET Core configuration, no dedicated config service), plus the shared, settable
    /// ICurrentTenantAccessor both Api and Worker populate per-request/per-job. Callers that need a
    /// different provider (e.g. SQLite in unit tests) configure DbContextOptions themselves instead
    /// of calling this method. Also registers IObjectStorageClient, reading the "ObjectStorage"
    /// configuration section — see ADR "Object storage client abstraction (foundation slice)".
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var configuredConnectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing required 'ConnectionStrings:Default' configuration value.");

        // Applied HERE rather than where the connection string is configured, because there are two sources: an
        // operator's own value, and one OpenBaoSecretsReader rebuilds at runtime from a template plus a dynamic
        // credential. This is the single point both pass through. See DatabasePoolCeiling for why an uncapped
        // pool takes the whole deployment down rather than merely being untidy (#750).
        var (connectionString, maxPoolSize, poolSource) = DatabasePoolCeiling.Apply(
            configuredConnectionString, configuration.GetValue<int?>("Database:MaxPoolSize"));

        // The app-wide clock is the real system clock for EVERYONE — including OpenIddict/auth, which must track
        // real time: a frozen past instant makes issued tokens/cookies look already-expired to a real-time browser
        // and breaks interactive login. TryAdd so a test host can still substitute its own wall clock.
        services.TryAddSingleton(TimeProvider.System);

        // A SEPARATE, keyed clock used ONLY for demo-seed data + audit-event timestamps (the demo seed and
        // AuditRecorder resolve it by the "demo-clock" key). It is a FIXED instant when Demo:Clock is a parseable
        // date — set only by the manual-capture harness (ADR 0510) — so the manual's time-sensitive screens
        // (audit / tasks / my-work) are byte-stable, WITHOUT freezing the auth clock. Everywhere else it is System.
        var demoClock = DateTimeOffset.TryParse(configuration["Demo:Clock"], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var fixedNow)
            ? (TimeProvider)new FixedTimeProvider(fixedNow)
            : TimeProvider.System;
        services.AddKeyedSingleton("demo-clock", demoClock);

        services.AddDbContext<SimplArchiveDbContext>(options => options.UseNpgsql(connectionString));

        // Startup states the effective ceiling (Program.cs). The number is otherwise invisible — it lives inside
        // a connection string nobody may print, because that string carries the password.
        services.AddSingleton(new DatabasePoolInfo(maxPoolSize, poolSource));

        // Registered as their own concrete type too, in addition to the read-only interface, so Api
        // middleware/Worker's job-processing loop can depend on the concrete settable class while every
        // other consumer (e.g. SimplArchiveDbContext) only ever sees the read-only interface — see ADR
        // "ServiceAccount request authentication foundation".
        services.AddScoped<CurrentTenantAccessor>();
        services.AddScoped<ICurrentTenantAccessor>(sp => sp.GetRequiredService<CurrentTenantAccessor>());
        services.AddScoped<CurrentServiceAccountAccessor>();
        services.AddScoped<ICurrentServiceAccountAccessor>(sp => sp.GetRequiredService<CurrentServiceAccountAccessor>());
        services.AddScoped<CurrentPlatformAdministratorAccessor>();
        services.AddScoped<ICurrentPlatformAdministratorAccessor>(sp => sp.GetRequiredService<CurrentPlatformAdministratorAccessor>());
        services.AddScoped<CurrentUserAccessor>();
        services.AddScoped<ICurrentUserAccessor>(sp => sp.GetRequiredService<CurrentUserAccessor>());

        services.AddScoped<CurrentImpersonationAccessor>();
        services.AddScoped<ICurrentImpersonationAccessor>(sp => sp.GetRequiredService<CurrentImpersonationAccessor>());

        // Scoped, and SHARED between the SaveChanges invariant and the Api: what a folder admits must be one
        // answer, not two that agree today (#673, ADR 0655).
        services.AddScoped<IMaskContainmentProvider, MaskContainmentProvider>();

        services.AddScoped<IEffectiveRightsCalculator, EffectiveRightsCalculator>();
        services.AddScoped<IUserSystemRightsResolver, UserSystemRightsResolver>();
        services.AddScoped<IClearanceResolver, ClearanceResolver>();
        services.AddScoped<IStorageQuotaService, Storage.StorageQuotaService>();
        services.AddScoped<ILegalHoldService, LegalHolds.LegalHoldService>();
        services.AddScoped<IRetentionService, Retention.RetentionService>();
        services.AddOptions<Retention.RetentionSweepOptions>().Bind(configuration.GetSection("Retention"));
        services.AddHostedService<Retention.RetentionWorker>();
        services.AddScoped<IStaleCheckoutService, Checkout.StaleCheckoutService>();
        services.AddHostedService<Checkout.StaleCheckoutWorker>();

        // Empties the ephemeral mail prefix (#640): Junk/Trash past their window, plus the objects filing left
        // behind. Registered as itself too, so a test can drive one pass without waiting for the timer.
        services.AddSingleton<Mail.EphemeralMailSweepWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<Mail.EphemeralMailSweepWorker>());

        // The intray ingest pipeline (ADR 0576). REGISTRATION ORDER IS THE PIPELINE ORDER: straightening must
        // run before patch-code detection (#492), because a patch code is horizontal bars read by a projection
        // profile and two degrees of rotation flattens it. Adding a processor here is choosing where in the
        // sequence it runs, which is why they are listed rather than discovered.
        services.AddScoped<Intray.IIntrayIngestProcessor, Intray.StraightenIngestProcessor>();
        services.AddScoped<Intray.IIntrayIngestProcessor, Intray.PatchCodeIngestProcessor>();
        services.AddScoped<Intray.IntrayIngestPipeline>();
        services.AddHostedService<Intray.IntrayIngestSweepWorker>();
        services.AddScoped<IWormLockService, Worm.WormLockService>();

        // TOTP-secret encryption (ADR "MFA require-policy + TOTP secret encryption"): OpenBao transit when
        // configured, else a pass-through (dev/tests keep plaintext). Singleton so the AppRole token is cached.
        var openBaoAddress = configuration["OpenBao:Address"];
        if (!string.IsNullOrWhiteSpace(openBaoAddress))
        {
            services.AddSingleton<ITransitEncryptor>(new Secrets.OpenBaoTransitEncryptor(
                openBaoAddress,
                configuration["OpenBao:RoleId"] ?? "",
                configuration["OpenBao:SecretId"] ?? "",
                configuration["OpenBao:TransitKey"] ?? "simplarchive-mfa"));
        }
        else
        {
            services.AddSingleton<ITransitEncryptor, Secrets.NullTransitEncryptor>();
        }
        services.AddScoped<IAuditRecorder, Audit.AuditRecorder>();
        services.AddScoped<IAuditChainVerifier, Audit.AuditChainVerifier>();
        services.AddScoped<IAuditWormVerifier, Audit.AuditWormVerifier>();
        services.AddScoped<IAuditRetentionService, Audit.AuditRetentionService>();
        services.AddHostedService<Audit.AuditRetentionWorker>();
        services.AddScoped<IAuditWormArchiver, Audit.AuditWormArchiver>();
        services.AddHostedService<Audit.AuditWormWorker>();

        // What this installation may call outbound (ADR 0717) — the one answer shared by every sink that
        // accepts a caller-supplied URL. A singleton: it parses its allowlist once and holds no per-request
        // state.
        services.Configure<Http.OutboundHttpOptions>(configuration.GetSection(Http.OutboundHttpOptions.SectionName));
        services.AddSingleton<IOutboundAddressPolicy, Http.OutboundAddressPolicy>();

        // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). Registered unconditionally — the worker
        // is idle until a tenant configures a webhook URL; the HTTP sender is a typed client.
        //
        // Its handler is the GUARDED one (ADR 0717): the URL is a tenant administrator's, so the request has to
        // re-resolve and pin at connect time and must not follow a redirect. Registration-time validation alone
        // is bypassed by a name that answers publicly while it is being saved.
        services.AddScoped<IAuditWebhookDispatcher, Audit.AuditWebhookDispatcher>();
        services.AddHttpClient<IAuditWebhookSender, Audit.HttpAuditWebhookSender>()
            .ConfigurePrimaryHttpMessageHandler(provider =>
                Http.GuardedOutboundHandler.Create(provider.GetRequiredService<IOutboundAddressPolicy>()));
        services.AddHostedService<Audit.AuditWebhookWorker>();
        services.AddScoped<ISensitivityLabelSeeder, Documents.SensitivityLabelSeeder>();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // Default no-op real-time notifier (ADR "Real-time notifications (SignalR)"); the Api overrides this with
        // a SignalR hub-context broadcaster after AddInfrastructure.
        services.AddSingleton<IRealtimeNotifier, Notifications.NullRealtimeNotifier>();

        // Mail-domain verification (#667). Singleton: the lookup client keeps its own sockets and resolver
        // list, and building one per request would re-read the host's DNS configuration on every check.
        services.AddSingleton<IDnsTxtLookup, Dns.DnsTxtLookup>();
        services.AddScoped<IWorkflowEscalationService, Workflow.WorkflowEscalationService>();
        services.AddHostedService<Workflow.WorkflowEscalationWorker>();

        // Document reminders (Wiedervorlage) — a background sweep fires due reminders (ADR "Document reminders").
        services.AddScoped<IDocumentReminderService, Reminders.DocumentReminderService>();
        services.AddHostedService<Reminders.DocumentReminderWorker>();

        // Email notifications (ADR "Email notifications (SMTP)"): a background sweep emails the not-yet-emailed
        // Notification rows via MailKit. Gated on Smtp:Host the same "unset → disabled" way as the sidecars —
        // configured → real SMTP sender + the worker; unconfigured → a log-only sender and no worker (so tests /
        // SMTP-less deployments don't send). The dispatcher is registered either way so it's directly testable.
        services.AddOptions<Notifications.SmtpOptions>().Bind(configuration.GetSection("Smtp"));
        services.AddScoped<IEmailNotificationDispatcher, Notifications.EmailNotificationDispatcher>();
        if (!string.IsNullOrWhiteSpace(configuration["Smtp:Host"]))
        {
            services.AddScoped<IEmailSender, Notifications.SmtpEmailSender>();
            services.AddHostedService<Notifications.EmailNotificationWorker>();
        }
        else
        {
            services.AddScoped<IEmailSender, Notifications.NullEmailSender>();
        }
        services.AddScoped<IWellKnownMaskSeeder, WellKnownMaskSeeder>();

        // Search (ADR 0011/0249): OpenSearch full-text when OpenSearch:Url is configured, else the Postgres
        // metadata-only fallback with a no-op indexer — so the stack still runs without a search engine.
        // SearchReindexState is registered unconditionally (the reindex endpoint depends on it, ADR 0139).
        services.AddSingleton<SearchReindexState>();

        var openSearchUrl = configuration["OpenSearch:Url"];
        if (!string.IsNullOrWhiteSpace(openSearchUrl))
        {
            services.AddHttpClient<OpenSearchService>(c => c.BaseAddress = new Uri(openSearchUrl));
            services.AddScoped<ISearchService>(sp => sp.GetRequiredService<OpenSearchService>());

            services.AddHttpClient<OpenSearchDocumentIndexer>(c => c.BaseAddress = new Uri(openSearchUrl));
            services.AddScoped<IDocumentIndexer>(sp => sp.GetRequiredService<OpenSearchDocumentIndexer>());

            // Async indexing (ADR "Async indexing", 0011): controllers enqueue to the outbox; a hosted worker
            // drains it off the request path.
            services.AddScoped<IDocumentIndexQueue, SearchIndexOutboxQueue>();
            services.AddHostedService<SearchIndexWorker>();

            // Blue-green full rebuild + startup backfill (ADR 0139 / 0253's deferred reindex-all).
            services.AddHttpClient<OpenSearchIndexRebuilder>(c => c.BaseAddress = new Uri(openSearchUrl));
            services.AddHostedService<SearchReindexService>();
        }
        else
        {
            services.AddScoped<ISearchService, MetadataSearchService>();
            services.AddSingleton<IDocumentIndexer, NullDocumentIndexer>();
            services.AddScoped<IDocumentIndexQueue, NullDocumentIndexQueue>();
        }

        // Tika sidecar — shared by search and preview, so wired independently of OpenSearch: document text
        // extraction for the search index (ADR "OpenSearch full-text slice 1"), OCR for scanned documents (ADR
        // "OCR for scanned documents"), and Tesseract hOCR word layout for hit-overlay (ADR "Search hit
        // overlay"). ITextExtractor is only consumed by the OpenSearch indexer; registering it without a search
        // engine is a harmless no-op.
        var tikaUrl = configuration["Tika:Url"];
        if (!string.IsNullOrWhiteSpace(tikaUrl))
        {
            // OCR languages (ADR "OCR for scanned documents") — a Tesseract language string; defaults to the
            // official Swiss languages + English. Requires the corresponding tessdata in the Tika image (the
            // tika:*-full image bundles eng/deu/fra/ita/spa/jpn).
            var ocrLanguages = configuration["Tika:OcrLanguages"];
            services.AddSingleton(new TikaOptions(
                string.IsNullOrWhiteSpace(ocrLanguages) ? "eng+deu+fra+ita" : ocrLanguages));
            services.AddHttpClient<ITextExtractor, TikaTextExtractor>(c => c.BaseAddress = new Uri(tikaUrl));
            services.AddHttpClient<IImageTextLayoutExtractor, TikaHocrExtractor>(c => c.BaseAddress = new Uri(tikaUrl));
        }
        else
        {
            services.AddSingleton<ITextExtractor, NullTextExtractor>();
            services.AddSingleton<IImageTextLayoutExtractor, NullImageTextLayoutExtractor>();
        }

        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection("ObjectStorage"))
            .ValidateOnStart();
        services.AddSingleton<IObjectStorageClient, S3ObjectStorageClient>();
        services.AddScoped<IDocumentPreviewService, RenditionService>();

        // Inline unified text diff between two document versions (ADR "Document version comparison") — reuses the
        // object store + ITextExtractor (Tika/Null).
        services.AddScoped<IDocumentVersionComparer, Comparison.DocumentVersionComparer>();

        // Per-page word boxes for search hit-overlay (ADR "Search hit overlay"): OCR (hOCR) for images, PdfPig
        // for PDFs, run against the displayed rendition and cached as a sidecar object.
        services.AddScoped<IDocumentTextLayoutService, DocumentTextLayoutService>();

        // Email envelope parsing for auto-classification (ADR "Email auto-classification") — no Gotenberg
        // needed (header-only, unlike the preview converter), so registered unconditionally.
        services.AddSingleton<IEmailMetadataExtractor, EmailMetadataExtractor>();

        // On-demand .zip reading for browsing archive contents (ADR "Zip file browsing").
        services.AddSingleton<IArchiveReader, ZipArchiveReader>();

        // Preview conversion via the Gotenberg sidecar: office docs -> PDF (LibreOffice route, ADR "Office
        // document preview via Gotenberg") and emails -> PDF (parse then Chromium HTML route, ADR "Email
        // (.eml/.msg) preview"). Both use a typed HttpClient pointed at Gotenberg. Not ValidateOnStart: when
        // Gotenberg:Url is unset the client has no BaseAddress and the converter throws, which
        // RenditionService catches to offer no preview rather than fail.
        services.AddOptions<GotenbergOptions>().Bind(configuration.GetSection("Gotenberg"));

        static void ConfigureGotenbergClient(IServiceProvider sp, HttpClient client)
        {
            var url = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GotenbergOptions>>().Value.Url;
            if (!string.IsNullOrWhiteSpace(url))
            {
                client.BaseAddress = new Uri(url);
            }

            // LibreOffice conversion (especially a cold soffice start) can take several seconds.
            client.Timeout = TimeSpan.FromSeconds(120);
        }

        services.AddHttpClient<IOfficeConverter, GotenbergOfficeConverter>(ConfigureGotenbergClient);
        services.AddHttpClient<IEmailConverter, EmailConverter>(ConfigureGotenbergClient);
        services.AddHttpClient<IMarkdownConverter, MarkdownConverter>(ConfigureGotenbergClient);
        services.AddHttpClient<IHtmlConverter, HtmlConverter>(ConfigureGotenbergClient);

        // Searchable-PDF successor for TIFFs (ADR "Searchable PDF successor for TIFFs"): when the OCR sidecar
        // is configured, a TIFF version's finalize enqueues a job that OCRs it into a searchable PDF stored as
        // the next version. When Ocr:Url is unset the whole workflow is a no-op (converter + queue are Null,
        // so nothing is enqueued and no worker runs) — tests and OCR-less deployments are unaffected.
        var ocrUrl = configuration["Ocr:Url"];
        if (!string.IsNullOrWhiteSpace(ocrUrl))
        {
            // OCR languages are resolved per conversion from the version override / tenant default (ADR
            // "Per-tenant / per-version OCR languages") — no global config knob.
            services.AddHttpClient<ISearchablePdfConverter, OcrmypdfConverter>(client =>
            {
                client.BaseAddress = new Uri(ocrUrl);
                client.Timeout = TimeSpan.FromMinutes(10); // multi-page OCR can be slow
            });
            services.AddScoped<ISearchablePdfQueue, SearchablePdfOutboxQueue>();
            services.AddHostedService<SearchablePdfWorker>();

            // The same sidecar draws external-link thumbnails (issue #476) — it is the only container in the
            // deployment with a PDF rasteriser, since the Api image is musl and PDFium/NetVips ship glibc-only
            // PDF support. A shorter timeout than OCR's: one page at 300px is quick, and the sharer is waiting.
            services.AddHttpClient<IDocumentThumbnailService, DocumentThumbnailService>(client =>
            {
                client.BaseAddress = new Uri(ocrUrl);
                client.Timeout = TimeSpan.FromSeconds(60);
            });

            // And it is the only image that can read a patch code, for the same reason (#492). A whole batch is
            // rasterised page by page, so the timeout is OCR-sized rather than thumbnail-sized.
            services.AddHttpClient<IPatchCodeDetector, SidecarPatchCodeDetector>(client =>
            {
                client.BaseAddress = new Uri(ocrUrl);
                client.Timeout = TimeSpan.FromMinutes(5);
            });
        }
        else
        {
            services.AddSingleton<ISearchablePdfConverter, NullSearchablePdfConverter>();
            services.AddScoped<ISearchablePdfQueue, NullSearchablePdfQueue>();
            services.AddSingleton<IPatchCodeDetector, NullPatchCodeDetector>();

            // No sidecar, no thumbnails. Registered with no BaseAddress so the service short-circuits, which
            // keeps every test and OCR-less deployment on the pre-#476 landing page rather than failing.
            services.AddHttpClient<IDocumentThumbnailService, DocumentThumbnailService>();
        }

        return services;
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Missing required 'ConnectionStrings:Default' configuration value.");

        services.AddDbContext<SimplArchiveDbContext>(options => options.UseNpgsql(connectionString));

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

        services.AddScoped<IEffectiveRightsCalculator, EffectiveRightsCalculator>();
        services.AddScoped<IUserSystemRightsResolver, UserSystemRightsResolver>();
        services.AddScoped<IClearanceResolver, ClearanceResolver>();
        services.AddScoped<IStorageQuotaService, Storage.StorageQuotaService>();
        services.AddScoped<ILegalHoldService, LegalHolds.LegalHoldService>();
        services.AddScoped<IRetentionService, Retention.RetentionService>();
        services.AddHostedService<Retention.RetentionWorker>();
        services.AddScoped<IStaleCheckoutService, Checkout.StaleCheckoutService>();
        services.AddHostedService<Checkout.StaleCheckoutWorker>();
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

        // Audit webhook / SIEM streaming (ADR "Audit webhook streaming"). Registered unconditionally — the worker
        // is idle until a tenant configures a webhook URL; the HTTP sender is a typed client.
        services.AddScoped<IAuditWebhookDispatcher, Audit.AuditWebhookDispatcher>();
        services.AddHttpClient<IAuditWebhookSender, Audit.HttpAuditWebhookSender>();
        services.AddHostedService<Audit.AuditWebhookWorker>();
        services.AddScoped<ISensitivityLabelSeeder, Documents.SensitivityLabelSeeder>();
        services.AddScoped<INotificationService, Notifications.NotificationService>();
        // Default no-op real-time notifier (ADR "Real-time notifications (SignalR)"); the Api overrides this with
        // a SignalR hub-context broadcaster after AddInfrastructure.
        services.AddSingleton<IRealtimeNotifier, Notifications.NullRealtimeNotifier>();
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
        }
        else
        {
            services.AddSingleton<ISearchablePdfConverter, NullSearchablePdfConverter>();
            services.AddScoped<ISearchablePdfQueue, NullSearchablePdfQueue>();
        }

        return services;
    }
}

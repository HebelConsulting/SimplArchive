using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Groups;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

// Env-driven idempotent demo-data seed (Docker Compose / kiosk only) — provisions a demo tenant plus a
// TenantAdministrator with a KNOWN password and a realistic sample tree, so a visitor can browse straight to the
// UI, log in, and actually see content (ADR "Compose demo-data seeding" / 0214). Extracted from Program.cs
// (issue #354) as it grew a full Business Years / Contracts / General tree with varied document types, references,
// two extra users and a shared "Scan Team" group inbox (the group-inbox showcase, ADR 0532).
//
// Gated on the demo config being present AND the tenant not already existing, so it's a no-op on restart and in
// every environment that doesn't configure it (default appsettings ships the keys empty). All content is
// public-safe (fictional companies; no real customer data).
public static class DemoDataSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var demoTenantName = configuration["Demo:Tenant:Name"];
        var demoAdminEmail = configuration["Demo:Administrator:Email"];
        var demoAdminPassword = configuration["Demo:Administrator:Password"];

        if (string.IsNullOrWhiteSpace(demoTenantName)
            || string.IsNullOrWhiteSpace(demoAdminEmail)
            || string.IsNullOrWhiteSpace(demoAdminPassword))
        {
            return;
        }

        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();

        // Demo timestamps resolve from the keyed "demo-clock" (a fixed instant only under Demo:Clock), NOT the
        // app-wide TimeProvider — so the manual's audit/tasks/my-work screens are byte-stable run-to-run (ADR 0510)
        // while auth keeps the real clock. Production/kiosk leave Demo:Clock unset, so this is System there too.
        var clock = services.GetRequiredKeyedService<TimeProvider>("demo-clock");

        if (await dbContext.Tenants.AnyAsync(t => t.Name == demoTenantName && t.Status == TenantStatus.Active))
        {
            return;
        }

        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            demoTenantName,
            demoAdminEmail,
            configuration["Demo:Administrator:DisplayName"] ?? "Demo Admin",
            configuration["Demo:RepositoryName"],
            demoAdminPassword);

        // The sample tree reads masks/field definitions and relies on SaveChanges' required-field validation, all
        // tenant-filtered — so set the tenant on the accessor the DbContext reads from (nothing did, since this
        // isn't a request). Provisioning above runs before this with no tenant set, like the platform-admin path.
        services.GetRequiredService<CurrentTenantAccessor>().TenantId = provisioned.TenantId;

        await SeedAsync(services, dbContext, clock, provisioned, demoAdminPassword);
    }

    private static async Task SeedAsync(
        IServiceProvider services, SimplArchiveDbContext dbContext, TimeProvider clock,
        ProvisionedTenant provisioned, string demoPassword)
    {
        var tenantId = provisioned.TenantId;
        var adminId = provisioned.AdministratorId;
        var repositoryId = provisioned.RepositoryId;
        var objectStorage = services.GetRequiredService<IObjectStorageClient>();
        // Demo versions are confirmed through the SAME finalizer an interactive upload uses (ADR 0545), so the
        // demo exercises the real path and gets its automatic chat entries instead of a silent parallel one.
        var finalizer = services.GetRequiredService<Documents.DocumentFinalizer>();
        var assembly = typeof(DemoDataSeeder).Assembly;
        var now = clock.GetUtcNow();

        // Give the demo tenant a 250 MB storage quota (ADR "Per-tenant storage quota") — production tenants are
        // provisioned with no quota (unlimited). Showcases the quota UI + enforcement from the single login.
        var demoTenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        demoTenant.StorageQuotaBytes = 250L * 1024 * 1024;

        // External links ON for the DEMO tenant only (ADR 0546, issue #405). The product default stays false, so a
        // real tenant still opts in deliberately — but the switch is checked at ACCESS time, so leaving it off here
        // would make the seeded link below answer 410 to every visitor. A showcase that ships the feature switched
        // off is not showing it.
        demoTenant.AllowExternalLinks = true;
        // The demo is a SHOWCASE, so it opts into revealing an existing link's URL (issue #412): the URL is the
        // one artefact that makes "share a document with someone who has no account" concrete, and a visitor
        // could not otherwise see one without creating it. Safe precisely here — the credentials are published,
        // so a token exposed to a visitor gives away nothing they cannot already reach. Off for every real
        // tenant by default.
        demoTenant.ShowExternalLinkUrl = true;
        await dbContext.SaveChangesAsync();

        var folderMaskVersionId = await FolderMask.CurrentVersionIdAsync(dbContext, CancellationToken.None);

        // The "Basic Entry" well-known mask (document type). It no longer has a required field (ADR "Drop redundant
        // Short Description / Doc Date mask fields"), so no index data needs filling first. Give it a review SLA
        // (ADR "Workflow escalation / SLA reminders") + a retention period (ADR "Retention policies") so the demo
        // document appears on the Tasks + Retention schedules.
        var basicEntryVersion = await dbContext.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent);
        basicEntryVersion.ReviewSlaDays = 7;
        basicEntryVersion.RetentionYears = 7;
        await dbContext.SaveChangesAsync();

        // ── The realistic filing tree (issue #354): Business Years / Contracts / General. Built first so the
        // "Contracts / Acme Corp" customer folder + the month folders exist for the showcase documents below. ────
        var (acmeCorp, march2026, telekomAgreement) = await SeedRichTreeAsync(dbContext, objectStorage, assembly, tenantId, repositoryId, adminId, now, basicEntryVersion.Id, folderMaskVersionId, finalizer);

        // ── Showcase customer "Contracts / Acme Corp": an invoice ("Invoice 2026-003", document-dated March 2026)
        // with a highlight + sticky note + stamp and in the approval workflow — so the demo login lands on live
        // workflow + annotations — and *referenced* (multi-filed) into its Business-Years month; plus an offer
        // ("Offer 2026-014", document-dated January 2026) with two PDF revisions for the Compare-versions feature. ─
        var invoice = await AddDocumentAsync(dbContext, objectStorage, assembly, tenantId, acmeCorp.Id,
            "Invoice 2026-003", adminId, now, basicEntryVersion.Id, "DemoInvoice.pdf", ".pdf", "application/pdf",
            new DateOnly(2026, 3, 3), finalizer);
        var invoiceVersion = await dbContext.DocumentVersions.SingleAsync(v => v.DocumentId == invoice.Id);

        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Highlight, 0.575, 0.490, 0.345, 0.030, string.Empty, "#ffd54a");
        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Note, 0.085, 0.300, 0.300, 0.085, "Line 1: price checked against the framework agreement ✓", "#fff59d");
        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Stamp, 0.680, 0.120, 0.230, 0.090, "APPROVED", "#2e7d32");
        await dbContext.SaveChangesAsync();

        await AddInReviewWorkflowAsync(dbContext, tenantId, invoiceVersion.Id, adminId, now);

        // Multi-file the invoice into its Business-Years month — a shortcut in 2026 / 03 March (ADR "…move and reference").
        await AddReferenceAsync(dbContext, tenantId, march2026.Id, invoice.Id, adminId, now);

        var offer = await AddDocumentAsync(dbContext, objectStorage, assembly, tenantId, acmeCorp.Id,
            "Offer 2026-014", adminId, now, basicEntryVersion.Id, "DemoOfferV1.pdf", ".pdf", "application/pdf",
            new DateOnly(2026, 1, 14), finalizer, "Initial draft sent to the customer.");
        offer.CurrentVersionId = (await AddVersionAsync(dbContext, objectStorage, assembly, offer, adminId, now,
            offer.StorageFolderId, "DemoOfferV2.pdf", ".pdf", "application/pdf",
            new DateOnly(2026, 1, 14), finalizer, "Price corrected after the framework-agreement review.")).Id;
        await dbContext.SaveChangesAsync();

        // A small colour-coded tag catalog + a couple of tags on the invoice (ADR "Tag controlled vocabulary" 0422).
        foreach (var (tagName, tagColor) in new[] { ("invoice", "#e53935"), ("contract", "#1e88e5"), ("urgent", "#fb8c00"), ("reviewed", "#43a047") })
        {
            dbContext.TagDefinitions.Add(new TagDefinition { Id = Guid.NewGuid(), TenantId = tenantId, Name = tagName, Color = tagColor, CreatedAt = now });
        }

        foreach (var tagName in new[] { "invoice", "reviewed" })
        {
            dbContext.DocumentTags.Add(new DocumentTag { Id = Guid.NewGuid(), TenantId = tenantId, DocumentId = invoice.Id, Tag = tagName, CreatedAt = now });
        }

        await dbContext.SaveChangesAsync();

        // ── Two extra users + a shared "Scan Team" group inbox (the group-inbox showcase, ADR 0532). ─────────────
        var (annaId, _) = await SeedTeamAsync(
            dbContext, objectStorage, assembly, tenantId, repositoryId, adminId, now, demoPassword, provisioned.AdministratorEmail);

        // A real conversation on the offer (issue #380). The thread already carries the automatic entries — filed,
        // each version saved (ADR 0545) — so this puts what PEOPLE said alongside them, which is what the pane
        // actually looks like in use.
        //
        // Two authors and a REPLY are both deliberate: the reply exercises threading (the external-system feed
        // export attaches a reply to its parent post, and a live run could not prove that path with an empty
        // thread), and a second author means the identity card is demonstrably per-person rather than always the
        // logged-in user.
        var offerThread = new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = offer.Id,
            Body = "Customer came back on the price — version 2 has the corrected figure.",
            CreatedByUserId = adminId,
            CreatedAt = now,
        };
        dbContext.ChatMessages.Add(offerThread);
        dbContext.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = offer.Id,
            ParentMessageId = offerThread.Id,
            Body = "Checked it against the framework agreement — the new figure is right.",
            CreatedByUserId = annaId,
            CreatedAt = now,
        });
        dbContext.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = invoice.Id,
            Body = "Approved for payment — see the stamp on page 1.",
            CreatedByUserId = adminId,
            CreatedAt = now,
        });
        await dbContext.SaveChangesAsync();

        await SeedExternalLinkAsync(dbContext, tenantId, telekomAgreement.Id, adminId, now);
    }

    // A live external link on the Telekom service agreement (ADR 0546, issue #405), so the feature is something a
    // visitor can click rather than read about. Three of its properties are deliberate:
    //
    // STATIC TOKEN. The kiosk re-seeds nightly from empty volumes, so a fresh random token would mint a new URL
    // every night and break every link already shared, bookmarked or written into a demo script. Derived from a
    // fixed string instead — see ExternalLinkToken.DeriveForDemoSeed for why that is safe here and nowhere else.
    //
    // 90 DAYS. Long enough that the nightly reset always refreshes it well before it lapses, so the demo is never
    // dead the morning after. (Comfortably inside the tenant's own 180-day cap.)
    //
    // UNLIMITED ACCESSES. The tenant default is 5, which five curious visitors would exhaust — the same
    // dead-demo problem in a different disguise, and one the reset would only fix the next day.
    private static async Task SeedExternalLinkAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid documentId, Guid adminId, DateTimeOffset now)
    {
        dbContext.ExternalLinks.Add(new ExternalLink
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            Token = ExternalLinkToken.DeriveForDemoSeed("simplarchive-demo-telekom-service-agreement-v1"),
            ExpiresAt = now.AddDays(90),
            // null = unlimited, NOT the tenant's ExternalLinkDefaultAccesses.
            MaxAccesses = null,
            CreatedByUserId = adminId,
            CreatedAt = now,
        });

        await dbContext.SaveChangesAsync();
    }

    // The business-filing tree from issue #354. Returns the "Contracts / Acme Corp" customer folder + the
    // "Business Years / 2026 / 03 March" month folder — the showcase invoice + offer are filed into Acme Corp and
    // the invoice is also referenced (multi-filed) into March by the caller.
    private static async Task<(Document AcmeCorp, Document March, Document TelekomAgreement)> SeedRichTreeAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid repositoryId, Guid adminId, DateTimeOffset now, Guid basicEntryVersionId, Guid? folderMaskVersionId,
        Documents.DocumentFinalizer finalizer)
    {
        async Task<Document> FolderAsync(Guid parentId, string name) =>
            await AddFolderAsync(dbContext, tenantId, parentId, name, adminId, now, folderMaskVersionId);

        Task<Document> DocAsync(Guid parentId, string name, string resource, string ext, string contentType, DateOnly date) =>
            AddDocumentAsync(dbContext, storage, assembly, tenantId, parentId, name, adminId, now, basicEntryVersionId, resource, ext, contentType, date, finalizer);

        // Business Years / 2026 / 01..12 <Month>.
        var businessYears = await FolderAsync(repositoryId, "Business Years");
        var year2026 = await FolderAsync(businessYears.Id, "2026");
        string[] months = ["01 January", "02 February", "03 March", "04 April", "05 May", "06 June", "07 July", "08 August", "09 September", "10 October", "11 November", "12 December"];
        var monthFolders = new Dictionary<int, Document>();
        for (var i = 0; i < months.Length; i++)
        {
            monthFolders[i + 1] = await FolderAsync(year2026.Id, months[i]);
        }

        // February holds the public-transport-ticket invoice (an .eml — exercises the email rendering path).
        await DocAsync(monthFolders[2].Id, "Invoice for public transport ticket", "DemoTransportTicket.eml", ".eml", "message/rfc822", new DateOnly(2026, 2, 9));

        // March holds the chocolate-gift invoice as a PDF with TWO versions + a highlight and a sticky note — a
        // second Compare-versions + annotations sample living inside the Business Years tree (for the manual).
        var chocolate = await DocAsync(monthFolders[3].Id, "Invoice for customer's chocolate gift", "DemoChocInvoiceV1.pdf", ".pdf", "application/pdf", new DateOnly(2026, 3, 16));
        var chocV2 = await AddVersionAsync(dbContext, storage, assembly, chocolate, adminId, now, chocolate.StorageFolderId, "DemoChocInvoiceV2.pdf", ".pdf", "application/pdf", new DateOnly(2026, 3, 16), finalizer,
            "Re-scanned — the first scan cut off the footer.");
        chocolate.CurrentVersionId = chocV2.Id;
        await dbContext.SaveChangesAsync();
        AddAnnotation(dbContext, tenantId, chocolate.Id, chocV2.Id, adminId, now, AnnotationKind.Highlight, 0.560, 0.470, 0.360, 0.030, string.Empty, "#ffd54a");
        AddAnnotation(dbContext, tenantId, chocolate.Id, chocV2.Id, adminId, now, AnnotationKind.Note, 0.085, 0.300, 0.300, 0.085, "v2: quantity corrected to 24 + gift wrapping added.", "#fff59d");
        await dbContext.SaveChangesAsync();

        // Contracts / …
        var contracts = await FolderAsync(repositoryId, "Contracts");
        // Acme Corp — a customer folder holding their offer (2-version compare showcase) + an invoice; filled by
        // the caller after this tree exists.
        var acmeCorp = await FolderAsync(contracts.Id, "Acme Corp");
        var contoso = await FolderAsync(contracts.Id, "Contoso Cloud");
        await DocAsync(contoso.Id, "Contoso Cloud — 2026 cost forecast", "DemoContosoForecast.xlsx", ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new DateOnly(2026, 1, 15));

        var telekom = await FolderAsync(contracts.Id, "MyCountry Telekom");
        // Returned to the caller: this is the document the seeded external link shares (issue #405).
        var telekomAgreement = await DocAsync(telekom.Id, "MyCountry Telekom — service agreement", "DemoTelekomContract.pdf", ".pdf", "application/pdf", new DateOnly(2026, 1, 1));

        // The three monthly Telekom invoices live under Contracts/MyCountry Telekom/Invoices AND are *referenced*
        // (a shortcut, ADR "…move and reference") into the matching Business Years month — the multi-filing showcase.
        var telekomInvoices = await FolderAsync(telekom.Id, "Invoices");
        var invJan = await DocAsync(telekomInvoices.Id, "MyCountry Telekom invoice — January 2026", "DemoTelekomInvoiceJan.pdf", ".pdf", "application/pdf", new DateOnly(2026, 1, 1));
        var invFeb = await DocAsync(telekomInvoices.Id, "MyCountry Telekom invoice — February 2026", "DemoTelekomInvoiceFeb.pdf", ".pdf", "application/pdf", new DateOnly(2026, 2, 1));
        var invMar = await DocAsync(telekomInvoices.Id, "MyCountry Telekom invoice — March 2026", "DemoTelekomInvoiceMar.pdf", ".pdf", "application/pdf", new DateOnly(2026, 3, 1));
        await AddReferenceAsync(dbContext, tenantId, monthFolders[1].Id, invJan.Id, adminId, now);
        await AddReferenceAsync(dbContext, tenantId, monthFolders[2].Id, invFeb.Id, adminId, now);
        await AddReferenceAsync(dbContext, tenantId, monthFolders[3].Id, invMar.Id, adminId, now);

        var rental = await FolderAsync(contracts.Id, "Rental Agreement");
        await DocAsync(rental.Id, "Complaint letter — stairwell cleanliness", "DemoRentalComplaint.docx", ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new DateOnly(2026, 3, 12));

        // General / … (mostly filing folders; Templates holds the ODF templates).
        var general = await FolderAsync(repositoryId, "General");
        foreach (var name in new[] { "Accounting", "Authorities", "Banks", "Employees", "Insurances" })
        {
            await FolderAsync(general.Id, name);
        }

        var templates = await FolderAsync(general.Id, "Templates");
        await DocAsync(templates.Id, "Letterhead template", "DemoLetterheadTemplate.odt", ".odt", "application/vnd.oasis.opendocument.text", new DateOnly(2026, 1, 1));
        await DocAsync(templates.Id, "Invoice template", "DemoInvoiceTemplate.ods", ".ods", "application/vnd.oasis.opendocument.spreadsheet", new DateOnly(2026, 1, 1));

        return (acmeCorp, monthFolders[3], telekomAgreement);
    }

    // Two extra users (an editor + a clerk) and a shared "Scan Team" group with a seeded group-inbox item — so the
    // group-inbox feature (ADR 0532) is live on the demo login: the admin (CanManageInboxes) can open the users'
    // inboxes and the Scan Team group inbox shows an unfiled scan waiting to be picked up.
    private static async Task<(Guid AnnaId, Guid TomId)> SeedTeamAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid repositoryId, Guid adminId, DateTimeOffset now, string demoPassword, string adminEmail)
    {
        var hasher = new PasswordHasher<User>();

        // The extra logins take the ADMIN's domain rather than a hardcoded one, so a deployment that renames the
        // demo admin (the kiosk uses @simplarchive.dev, local Compose @simplarchive.local) doesn't end up handing
        // visitors three credentials straddling two domains — the READMEs list all three side by side (issue #432).
        var domain = adminEmail.Split('@') is [_, var d] && !string.IsNullOrWhiteSpace(d) ? d : "simplarchive.local";

        User MakeUser(string localPart, string displayName)
        {
            var user = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = $"{localPart}@{domain}", DisplayName = displayName, IsActive = true, CreatedAt = now };
            user.PasswordHash = hasher.HashPassword(user, demoPassword); // same known demo password, so they can log in too
            return user;
        }

        var anna = MakeUser("anna", "Anna Meyer");
        var tom = MakeUser("tom", "Tom Fischer");
        dbContext.Users.Add(anna);
        dbContext.Users.Add(tom);

        var scanTeam = new Group { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Scan Team" };
        dbContext.Groups.Add(scanTeam);
        await dbContext.SaveChangesAsync();

        // Admin + both users are members, so the admin sees the Scan Team group inbox on the demo login.
        foreach (var userId in new[] { adminId, anna.Id, tom.Id })
        {
            dbContext.GroupMemberships.Add(new GroupMembership { TenantId = tenantId, UserId = userId, GroupId = scanTeam.Id });
        }

        // Give the group read/work access to the demo repository so its members can actually see and file into it.
        dbContext.AclEntries.Add(new AclEntry
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = repositoryId,
            GroupId = scanTeam.Id,
            CanSee = true,
            CanReadContent = true,
            CanEditContent = true,
            CanEditIndexData = true,
            CanCreateSubItems = true,
            CreatedAt = now,
        });
        await dbContext.SaveChangesAsync();

        // One unfiled scan sitting in the Scan Team group inbox (a raw object under the group's inbox prefix, ADR
        // 0532) — so the group-inbox view isn't empty and "pick it up" / Send-to are demonstrable live.
        var scanBytes = await ReadResourceAsync(assembly, "DemoTelekomInvoiceMar.pdf");
        using var content = new MemoryStream(scanBytes);
        await storage.PutObjectAsync($"tenants/{tenantId}/groups/{scanTeam.Id}/inbox/scan-2026-03-inbox.pdf", content, "application/pdf");

        return (anna.Id, tom.Id);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<Document> AddFolderAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid parentId, string name, Guid adminId, DateTimeOffset at, Guid? folderMaskVersionId)
    {
        var folder = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = folderMaskVersionId,
            CreatedByUserId = adminId,
            CreatedAt = at,
        };
        dbContext.Documents.Add(folder);
        await dbContext.SaveChangesAsync();
        return folder;
    }

    private static async Task<Document> AddDocumentAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid parentId, string name, Guid adminId, DateTimeOffset at, Guid maskVersionId,
        string resourceName, string ext, string contentType, DateOnly documentDate,
        Documents.DocumentFinalizer finalizer, string? comment = null)
    {
        var storageFolderId = Guid.NewGuid();
        var document = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = parentId,
            Name = name,
            MaskVersionId = maskVersionId,
            CreatedByUserId = adminId,
            CreatedAt = at,
            StorageFolderId = storageFolderId,
        };
        dbContext.Documents.Add(document);
        await dbContext.SaveChangesAsync();

        await AddVersionAsync(dbContext, storage, assembly, document, adminId, at, storageFolderId, resourceName, ext, contentType, documentDate, finalizer, comment);
        return document;
    }

    private static async Task<DocumentVersion> AddVersionAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Document document, Guid adminId, DateTimeOffset at, Guid storageFolderId,
        string resourceName, string ext, string contentType, DateOnly documentDate,
        Documents.DocumentFinalizer finalizer, string? comment = null)
    {
        var bytes = await ReadResourceAsync(assembly, resourceName);
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, document.CreatedAt, storageFolderId, versionId, ext);
        using (var content = new MemoryStream(bytes))
        {
            await storage.PutObjectAsync(objectKey, content, contentType);
        }

        // Created PENDING and confirmed through DocumentFinalizer — the same path an interactive upload takes,
        // rather than a parallel one that writes a Confirmed row directly. That is what gives the demo its
        // automatic chat entries (ADR 0545): the seeder produced none precisely because it bypassed the finalizer.
        //
        // The finalizer owns what it computes, so the fields below are deliberately NOT set here: it assigns the
        // version number (from the existing versions, which yields the same sequence the callers used to pass),
        // hashes the blob server-side from object storage rather than trusting a value we hand it, and stamps
        // SizeBytes. It also counts the blob toward the demo tenant's 250 MB quota, so the quota UI now shows
        // real usage instead of zero.
        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Pending,
            ObjectKey = objectKey,
            CreatedByUserId = adminId,
            DocumentDate = documentDate,
            CreatedAt = at,
            // The check-in comment — why this version exists. Deliberately not set on every version: the feed
            // renders "Version N" with the comment beneath it when there is one and just "Version N" when there
            // isn't, and the demo should show both (issue #380).
            Comment = comment,
        };
        dbContext.DocumentVersions.Add(version);
        await dbContext.SaveChangesAsync();

        await finalizer.FinalizeAsync(version, CancellationToken.None);
        return version;
    }

    private static void AddAnnotation(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid documentId, Guid versionId, Guid adminId, DateTimeOffset at,
        AnnotationKind kind, double x, double y, double width, double height, string text, string color)
    {
        dbContext.DocumentAnnotations.Add(new DocumentAnnotation
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentId = documentId,
            DocumentVersionId = versionId,
            PageIndex = 0,
            Kind = kind,
            PositionX = x,
            PositionY = y,
            Width = width,
            Height = height,
            Text = text,
            Color = color,
            CreatedByUserId = adminId,
            CreatedAt = at,
            UpdatedAt = at,
        });
    }

    private static async Task AddInReviewWorkflowAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid versionId, Guid adminId, DateTimeOffset now)
    {
        var workflowState = new WorkflowState
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DocumentVersionId = versionId,
            Status = WorkflowStatus.InReview,
            AssignedToUserId = adminId,
            CreatedAt = now,
            UpdatedAt = now,
            // Hand-set overdue (deadline yesterday) so the demo Tasks tab shows the overdue badge and the
            // escalation sweep has something to act on (ADR "Workflow escalation / SLA reminders").
            DueAt = now.AddDays(-1),
        };
        dbContext.WorkflowStates.Add(workflowState);
        dbContext.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            WorkflowStateId = workflowState.Id,
            FromStatus = WorkflowStatus.Draft,
            ToStatus = WorkflowStatus.InReview,
            AssignedToUserId = adminId,
            PerformedByUserId = adminId,
            CreatedAt = now,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task AddReferenceAsync(
        SimplArchiveDbContext dbContext, Guid tenantId, Guid parentFolderId, Guid targetDocumentId, Guid adminId, DateTimeOffset at)
    {
        dbContext.DocumentReferences.Add(new DocumentReference
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentFolderId = parentFolderId,
            TargetDocumentId = targetDocumentId,
            CreatedByUserId = adminId,
            CreatedAt = at,
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<byte[]> ReadResourceAsync(Assembly assembly, string logicalName)
    {
        await using var resource = assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource {logicalName} was not found.");
        using var buffer = new MemoryStream();
        await resource.CopyToAsync(buffer);
        return buffer.ToArray();
    }
}

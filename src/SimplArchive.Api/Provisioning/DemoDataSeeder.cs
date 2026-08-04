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
        var assembly = typeof(DemoDataSeeder).Assembly;
        var now = clock.GetUtcNow();

        // Give the demo tenant a 250 MB storage quota (ADR "Per-tenant storage quota") — production tenants are
        // provisioned with no quota (unlimited). Showcases the quota UI + enforcement from the single login.
        var demoTenant = await dbContext.Tenants.SingleAsync(t => t.Id == tenantId);
        demoTenant.StorageQuotaBytes = 250L * 1024 * 1024;
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

        // ── The original showcase document: "Invoice 2025-001" in an "Invoices" folder, in the approval workflow
        // with a highlight + sticky note + stamp, so the demo login lands on live workflow + annotations. ─────────
        var invoicesFolder = await AddFolderAsync(dbContext, tenantId, repositoryId, "Invoices", adminId, now, folderMaskVersionId);
        var invoice = await AddDocumentAsync(dbContext, objectStorage, assembly, tenantId, invoicesFolder.Id,
            "Invoice 2025-001", adminId, now, basicEntryVersion.Id, "DemoInvoice.pdf", ".pdf", "application/pdf",
            DateOnly.FromDateTime(now.UtcDateTime));
        var invoiceVersion = await dbContext.DocumentVersions.SingleAsync(v => v.DocumentId == invoice.Id);

        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Highlight, 0.575, 0.490, 0.345, 0.030, string.Empty, "#ffd54a");
        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Note, 0.085, 0.300, 0.300, 0.085, "Line 1: price checked against the framework agreement ✓", "#fff59d");
        AddAnnotation(dbContext, tenantId, invoice.Id, invoiceVersion.Id, adminId, now, AnnotationKind.Stamp, 0.680, 0.120, 0.230, 0.090, "APPROVED", "#2e7d32");
        await dbContext.SaveChangesAsync();

        await AddInReviewWorkflowAsync(dbContext, tenantId, invoiceVersion.Id, adminId, now);

        // A second document ("Offer 2025-014") with TWO PDF revisions for the "Compare versions" feature (the two
        // revisions differ in real, extractable text so the inline diff highlights the changes).
        var offer = await AddDocumentAsync(dbContext, objectStorage, assembly, tenantId, repositoryId,
            "Offer 2025-014", adminId, now, basicEntryVersion.Id, "DemoOfferV1.pdf", ".pdf", "application/pdf",
            DateOnly.FromDateTime(now.UtcDateTime));
        offer.CurrentVersionId = (await AddVersionAsync(dbContext, objectStorage, assembly, offer, adminId, now,
            offer.StorageFolderId, "DemoOfferV2.pdf", ".pdf", "application/pdf",
            DateOnly.FromDateTime(now.UtcDateTime), 2)).Id;
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

        // ── The richer realistic tree (issue #354): Business Years / Contracts / General with varied file types
        // and a cross-folder reference showcase. ────────────────────────────────────────────────────────────────
        await SeedRichTreeAsync(dbContext, objectStorage, assembly, tenantId, repositoryId, adminId, now, basicEntryVersion.Id, folderMaskVersionId);

        // ── Two extra users + a shared "Scan Team" group inbox (the group-inbox showcase, ADR 0532). ─────────────
        await SeedTeamAsync(dbContext, objectStorage, assembly, tenantId, repositoryId, adminId, now, demoPassword);
    }

    // The business-filing tree from issue #354. Returns nothing — everything is persisted as it goes.
    private static async Task SeedRichTreeAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid repositoryId, Guid adminId, DateTimeOffset now, Guid basicEntryVersionId, Guid? folderMaskVersionId)
    {
        async Task<Document> FolderAsync(Guid parentId, string name) =>
            await AddFolderAsync(dbContext, tenantId, parentId, name, adminId, now, folderMaskVersionId);

        Task<Document> DocAsync(Guid parentId, string name, string resource, string ext, string contentType, DateOnly date) =>
            AddDocumentAsync(dbContext, storage, assembly, tenantId, parentId, name, adminId, now, basicEntryVersionId, resource, ext, contentType, date);

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
        var chocV2 = await AddVersionAsync(dbContext, storage, assembly, chocolate, adminId, now, chocolate.StorageFolderId, "DemoChocInvoiceV2.pdf", ".pdf", "application/pdf", new DateOnly(2026, 3, 16), 2);
        chocolate.CurrentVersionId = chocV2.Id;
        await dbContext.SaveChangesAsync();
        AddAnnotation(dbContext, tenantId, chocolate.Id, chocV2.Id, adminId, now, AnnotationKind.Highlight, 0.560, 0.470, 0.360, 0.030, string.Empty, "#ffd54a");
        AddAnnotation(dbContext, tenantId, chocolate.Id, chocV2.Id, adminId, now, AnnotationKind.Note, 0.085, 0.300, 0.300, 0.085, "v2: quantity corrected to 24 + gift wrapping added.", "#fff59d");
        await dbContext.SaveChangesAsync();

        // Contracts / …
        var contracts = await FolderAsync(repositoryId, "Contracts");
        var contoso = await FolderAsync(contracts.Id, "Contoso Cloud");
        await DocAsync(contoso.Id, "Contoso Cloud — 2026 cost forecast", "DemoContosoForecast.xlsx", ".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new DateOnly(2026, 1, 15));

        var telekom = await FolderAsync(contracts.Id, "MyCountry Telekom");
        await DocAsync(telekom.Id, "MyCountry Telekom — service agreement", "DemoTelekomContract.pdf", ".pdf", "application/pdf", new DateOnly(2026, 1, 1));

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
    }

    // Two extra users (an editor + a clerk) and a shared "Scan Team" group with a seeded group-inbox item — so the
    // group-inbox feature (ADR 0532) is live on the demo login: the admin (CanManageInboxes) can open the users'
    // inboxes and the Scan Team group inbox shows an unfiled scan waiting to be picked up.
    private static async Task SeedTeamAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Guid tenantId, Guid repositoryId, Guid adminId, DateTimeOffset now, string demoPassword)
    {
        var hasher = new PasswordHasher<User>();

        User MakeUser(string email, string displayName)
        {
            var user = new User { Id = Guid.NewGuid(), TenantId = tenantId, Email = email, DisplayName = displayName, IsActive = true, CreatedAt = now };
            user.PasswordHash = hasher.HashPassword(user, demoPassword); // same known demo password, so they can log in too
            return user;
        }

        var anna = MakeUser("anna@simplarchive.local", "Anna Meyer");
        var tom = MakeUser("tom@simplarchive.local", "Tom Fischer");
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
        string resourceName, string ext, string contentType, DateOnly documentDate)
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

        await AddVersionAsync(dbContext, storage, assembly, document, adminId, at, storageFolderId, resourceName, ext, contentType, documentDate, 1);
        return document;
    }

    private static async Task<DocumentVersion> AddVersionAsync(
        SimplArchiveDbContext dbContext, IObjectStorageClient storage, Assembly assembly,
        Document document, Guid adminId, DateTimeOffset at, Guid storageFolderId,
        string resourceName, string ext, string contentType, DateOnly documentDate, int versionNumber)
    {
        var bytes = await ReadResourceAsync(assembly, resourceName);
        var versionId = Guid.NewGuid();
        var objectKey = ObjectKeyBuilder.Build(document.TenantId, document.CreatedAt, storageFolderId, versionId, ext);
        using (var content = new MemoryStream(bytes))
        {
            await storage.PutObjectAsync(objectKey, content, contentType);
        }

        var version = new DocumentVersion
        {
            Id = versionId,
            TenantId = document.TenantId,
            DocumentId = document.Id,
            Status = DocumentVersionStatus.Confirmed,
            VersionNumber = versionNumber,
            Sha256Hash = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            ObjectKey = objectKey,
            CreatedByUserId = adminId,
            DocumentDate = documentDate,
            CreatedAt = at,
        };
        dbContext.DocumentVersions.Add(version);
        await dbContext.SaveChangesAsync();
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

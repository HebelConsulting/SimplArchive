using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Masks;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Provisioning;

// Env-driven idempotent seed of the ENCRYPTED demo tenant (ADR 0813) — the tenant the kiosk lists in
// Encryption:Tenants so the encryption service (SimplArchiveEncryption ADR 0007) can be exercised on a live
// stack WITHOUT touching the public demo tenant: same shape as DemoDataSeeder (ADR 0214) and
// InteropTenantSeeder (ADR 0585), a no-op unless the CryptoDemo:* config is present.
//
// It seeds the tenant with crypt (the tenant administrator) plus two plain users, florian and thomas — the
// people whose phones carry the S/MIME identities in the proof of concept — all three IMAP/WebDAV-enabled
// with the one configured password, and a couple of documents in the root repository so a mail client has
// something to fetch right after every nightly reset. Ids are deterministic (DemoId, #781) for the same
// reason the demo tenant's are: the kiosk reseeds from scratch nightly, and fresh GUIDs each morning would
// hand every caching IMAP client a brand-new server wearing yesterday's names.
public static class CryptoDemoSeeder
{
    public static async Task SeedIfConfiguredAsync(IServiceProvider services, IConfiguration configuration)
    {
        var tenantName = configuration["CryptoDemo:Tenant:Name"];
        var adminEmail = configuration["CryptoDemo:Administrator:Email"];
        var password = configuration["CryptoDemo:Password"];

        if (string.IsNullOrWhiteSpace(tenantName)
            || string.IsNullOrWhiteSpace(adminEmail)
            || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var dbContext = services.GetRequiredService<SimplArchiveDbContext>();

        // IgnoreQueryFilters: this runs at startup where the ambient tenant is whatever an earlier seeder
        // left set (or none), so the tenant filter would make this existence check a lie.
        if (await dbContext.Tenants.IgnoreQueryFilters()
                .AnyAsync(t => t.Name == tenantName && t.Status == TenantStatus.Active))
        {
            return;
        }

        var tenantId = DemoId.Root(tenantName);
        var provisioned = await services.GetRequiredService<ITenantProvisioningService>().ProvisionAsync(
            tenantName,
            adminEmail,
            configuration["CryptoDemo:Administrator:DisplayName"] ?? "Crypto Admin",
            configuration["CryptoDemo:RepositoryName"],
            password,
            idFor: slug => slug == "tenant" ? tenantId : DemoId.For(tenantId, slug));

        // The document seeding below reads masks and relies on SaveChanges' required-field validation, all
        // tenant-filtered — so point the accessor at the new tenant, like DemoDataSeeder does.
        services.GetRequiredService<CurrentTenantAccessor>().TenantId = provisioned.TenantId;

        var now = DateTimeOffset.UtcNow;
        var hasher = new PasswordHasher<User>();

        // The extra logins take the ADMIN's domain rather than a hardcoded one, same rule as the demo seed
        // (issue #432): the kiosk configures crypt@its-domain and florian/thomas ride along.
        var domain = adminEmail.Split('@') is [_, var d] && !string.IsNullOrWhiteSpace(d) ? d : "simplarchive.local";

        User MakeUser(string localPart, string displayName)
        {
            var user = new User
            {
                Id = DemoId.For(tenantId, $"user/{localPart}"),
                TenantId = provisioned.TenantId,
                Email = $"{localPart}@{domain}",
                DisplayName = displayName,
                IsActive = true,
                CreatedAt = now,
                // The proof of concept reads DOCUMENTS over IMAP, so the synthetic-message view must be on.
                ImapShowAllDocuments = true,
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            return user;
        }

        var florian = MakeUser("florian", "Florian");
        var thomas = MakeUser("thomas", "Thomas");
        dbContext.Users.Add(florian);
        dbContext.Users.Add(thomas);

        // The admin reads over IMAP too — and a phone signs in with the same password, so the IMAP/WebDAV
        // credentials are seeded for all three rather than minted per stack (the Interop lesson: a secret
        // shown once dies with every `down -v`).
        var admin = await dbContext.Users.SingleAsync(u => u.Id == provisioned.AdministratorId);
        admin.ImapShowAllDocuments = true;
        await dbContext.SaveChangesAsync();
        await DemoDataSeeder.EnableDavAndImapAsync(dbContext, hasher, password, [admin.Id, florian.Id, thomas.Id]);

        // Working rights on the root repository for both plain users — enough to see, read and file, so the
        // phones' IMAP trees show the repository and its documents.
        foreach (var (slug, user) in new[] { ("florian", florian), ("thomas", thomas) })
        {
            dbContext.AclEntries.Add(new AclEntry
            {
                Id = DemoId.For(tenantId, $"acl/repository/{slug}"),
                TenantId = provisioned.TenantId,
                DocumentId = provisioned.RepositoryId,
                UserId = user.Id,
                CanSee = true,
                CanReadContent = true,
                CanEditContent = true,
                CanEditIndexData = true,
                CanCreateSubItems = true,
                CreatedAt = now,
            });
        }

        await dbContext.SaveChangesAsync();

        // A couple of documents so IMAP serves content immediately — through the same finalizer path an
        // interactive upload takes (ADR 0545), reusing demo resources (content is irrelevant to the
        // encryption proof; that the bytes arrive as application/pkcs7-mime is the point).
        var storage = services.GetRequiredService<IObjectStorageClient>();
        var finalizer = services.GetRequiredService<Documents.DocumentFinalizer>();
        var assembly = typeof(DemoDataSeeder).Assembly;
        var basicEntryVersion = await dbContext.MaskVersions
            .SingleAsync(v => v.MaskId == WellKnownMaskIds.BasicEntry && v.IsCurrent);

        await DemoDataSeeder.AddDocumentAsync(dbContext, storage, assembly, provisioned.TenantId,
            provisioned.RepositoryId, "Board minutes 2026-03", admin.Id, now, basicEntryVersion.Id,
            "DemoInvoice.pdf", ".pdf", "application/pdf", new DateOnly(2026, 3, 3), finalizer);
        await DemoDataSeeder.AddDocumentAsync(dbContext, storage, assembly, provisioned.TenantId,
            provisioned.RepositoryId, "Salary review 2026", admin.Id, now, basicEntryVersion.Id,
            "DemoOfferV1.pdf", ".pdf", "application/pdf", new DateOnly(2026, 1, 14), finalizer);
    }
}

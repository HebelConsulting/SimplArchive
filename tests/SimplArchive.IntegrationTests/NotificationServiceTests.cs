using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// Verifies the notification service (ADR "Notifications (in-app, first slice)"): it writes a per-recipient
// notification, never notifies the actor about their own action, and no-ops when there's no tenant.
public class NotificationServiceTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, tenant);

    private static NotificationService CreateService(SimplArchiveDbContext db, CurrentTenantAccessor tenant, CurrentUserAccessor user) =>
        new(db, tenant, user);

    [Fact]
    public async Task Writes_a_notification_for_the_recipient_but_not_for_the_actor()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "actor@acme.test", DisplayName = "Actor", CreatedAt = DateTimeOffset.UtcNow };
        var recipient = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "rcpt@acme.test", DisplayName = "Recipient", CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); seed.Users.AddRange(actor, recipient); await seed.SaveChangesAsync(); }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyAsync(recipient.Id, NotificationType.ReviewAssigned, "Review requested", "Please review 'Invoice'.");
            await service.NotifyAsync(actor.Id, NotificationType.ReviewAssigned, "self", "should be skipped");
        }

        using var read = CreateContext(connection, tenantAccessor);
        var notifications = await read.Notifications.ToListAsync();
        var single = Assert.Single(notifications);
        Assert.Equal(recipient.Id, single.RecipientUserId);
        Assert.Equal(NotificationType.ReviewAssigned, single.Type);
        Assert.Null(single.ReadAt);
        Assert.DoesNotContain(notifications, n => n.RecipientUserId == actor.Id);
    }

    [Fact]
    public async Task No_op_when_no_tenant_is_set()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        // No tenant set on the accessor.
        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyAsync(Guid.NewGuid(), NotificationType.AccessGranted, "t", "b");
        }

        using var read = CreateContext(connection, tenantAccessor);
        Assert.Empty(await read.Notifications.IgnoreQueryFilters().ToListAsync());
    }

    // ADR "Document subscriptions": NotifyDocumentSubscribersAsync notifies each follower of the document,
    // except the acting user and anyone explicitly excluded (already notified by the primary trigger).
    [Fact]
    public async Task Notifies_document_followers_except_the_actor_and_the_excluded()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        User NewUser(string n) => new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"{n}@acme.test", DisplayName = n, CreatedAt = DateTimeOffset.UtcNow };
        var actor = NewUser("Actor");
        var follower = NewUser("Follower");
        var alreadyNotified = NewUser("AlreadyNotified");
        var nonFollower = NewUser("NonFollower");
        var doc = new SimplArchive.Domain.Documents.Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Report", CreatedByUserId = actor.Id, CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.AddRange(actor, follower, alreadyNotified, nonFollower);
            seed.Documents.Add(doc);
            // actor, follower and alreadyNotified all follow the document; nonFollower does not.
            foreach (var u in new[] { actor, follower, alreadyNotified })
            {
                seed.DocumentSubscriptions.Add(new SimplArchive.Domain.Documents.DocumentSubscription { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = u.Id, DocumentId = doc.Id, CreatedAt = DateTimeOffset.UtcNow });
            }
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyDocumentSubscribersAsync(doc.Id, NotificationType.SubscribedActivity, "Followed document updated", "A new version was added.", excludeUserIds: [alreadyNotified.Id]);
        }

        using var read = CreateContext(connection, tenantAccessor);
        var notifications = await read.Notifications.ToListAsync();
        var single = Assert.Single(notifications);           // only the follower is notified
        Assert.Equal(follower.Id, single.RecipientUserId);
        Assert.Equal(NotificationType.SubscribedActivity, single.Type);
        Assert.Equal(doc.Id, single.DocumentId);
        Assert.DoesNotContain(notifications, n => n.RecipientUserId == actor.Id);           // actor skipped
        Assert.DoesNotContain(notifications, n => n.RecipientUserId == alreadyNotified.Id); // excluded skipped
    }

    // ADR "Folder / subtree subscriptions": a change to a document notifies followers of the document AND of any
    // ancestor folder (following a folder = following its whole subtree), deduped so a user following both the
    // document and its parent folder is notified once.
    [Fact]
    public async Task Notifies_followers_of_ancestor_folders()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        User NewUser(string n) => new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = $"{n}@acme.test", DisplayName = n, CreatedAt = DateTimeOffset.UtcNow };
        var actor = NewUser("Actor");
        var folderFollower = NewUser("FolderFollower");
        var bothFollower = NewUser("BothFollower"); // follows the folder AND the leaf — must be notified once
        var outsider = NewUser("Outsider");

        // root  →  sub (folder)  →  leaf (the changed document)
        SimplArchive.Domain.Documents.Document Doc(string name, Guid? parent) =>
            new() { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = name, ParentId = parent, CreatedByUserId = actor.Id, CreatedAt = DateTimeOffset.UtcNow };
        var root = Doc("Root", null);
        var sub = Doc("Sub", root.Id);
        var leaf = Doc("Leaf", sub.Id);
        var unrelated = Doc("Unrelated", root.Id); // a sibling branch, not an ancestor of leaf

        using (var seed = CreateContext(connection, tenantAccessor))
        {
            seed.Tenants.Add(tenant);
            seed.Users.AddRange(actor, folderFollower, bothFollower, outsider);
            seed.Documents.AddRange(root, sub, leaf, unrelated);
            void Follow(User u, Guid docId) => seed.DocumentSubscriptions.Add(new SimplArchive.Domain.Documents.DocumentSubscription { Id = Guid.NewGuid(), TenantId = tenant.Id, UserId = u.Id, DocumentId = docId, CreatedAt = DateTimeOffset.UtcNow });
            Follow(folderFollower, sub.Id);   // follows the parent folder
            Follow(bothFollower, sub.Id);      // …and the leaf
            Follow(bothFollower, leaf.Id);
            Follow(outsider, unrelated.Id);    // follows a sibling branch — not in the leaf's ancestor chain
            await seed.SaveChangesAsync();
        }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyDocumentSubscribersAsync(leaf.Id, NotificationType.SubscribedActivity, "New document filed", "'Leaf' was filed.");
        }

        using var read = CreateContext(connection, tenantAccessor);
        var notifications = await read.Notifications.ToListAsync();
        Assert.Equal(2, notifications.Count); // folderFollower + bothFollower (once), not the outsider
        Assert.Contains(notifications, n => n.RecipientUserId == folderFollower.Id);
        Assert.Single(notifications, n => n.RecipientUserId == bothFollower.Id); // deduped despite two subscriptions
        Assert.All(notifications, n => Assert.Equal(leaf.Id, n.DocumentId));     // links to the changed document
    }

    // ADR "Notification digest / coalescing": a burst of coalescable events (ChatMessagePosted / SubscribedActivity) on
    // one document while its notification is unread merges into a single growing row (EventCount++); reading it
    // ends the digest; a non-coalescable type stays one-per-event; and an event past the window starts fresh.
    [Fact]
    public async Task Coalesces_a_burst_while_unread_and_resets_on_read()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "a@acme.test", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow };
        var recipient = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "r@acme.test", DisplayName = "R", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new SimplArchive.Domain.Documents.Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Doc", CreatedByUserId = actor.Id, CreatedAt = DateTimeOffset.UtcNow };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); seed.Users.AddRange(actor, recipient); seed.Documents.Add(doc); await seed.SaveChangesAsync(); }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            // Three comments on the same document → one coalesced row.
            await service.NotifyAsync(recipient.Id, NotificationType.ChatMessagePosted, "New comment", "c1", doc.Id);
            await service.NotifyAsync(recipient.Id, NotificationType.ChatMessagePosted, "New comment", "c2", doc.Id);
            await service.NotifyAsync(recipient.Id, NotificationType.ChatMessagePosted, "New comment", "c3", doc.Id);
            // A non-coalescable type on the same document stays its own row.
            await service.NotifyAsync(recipient.Id, NotificationType.AccessGranted, "Access granted", "g", doc.Id);
        }

        using (var read = CreateContext(connection, tenantAccessor))
        {
            var all = await read.Notifications.ToListAsync();
            Assert.Equal(2, all.Count); // one coalesced comment row + one access-granted row
            var comment = Assert.Single(all, n => n.Type == NotificationType.ChatMessagePosted);
            Assert.Equal(3, comment.EventCount);
            Assert.Single(all, n => n.Type == NotificationType.AccessGranted);

            // Mark the comment digest read → a further comment starts a fresh notification.
            comment.ReadAt = DateTimeOffset.UtcNow;
            await read.SaveChangesAsync();
        }

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyAsync(recipient.Id, NotificationType.ChatMessagePosted, "New comment", "c4", doc.Id);
        }

        using var final = CreateContext(connection, tenantAccessor);
        var comments = await final.Notifications.Where(n => n.Type == NotificationType.ChatMessagePosted).ToListAsync();
        Assert.Equal(2, comments.Count); // the read digest (×3) + a fresh unread one (×1)
        Assert.Single(comments, n => n.EventCount == 1 && n.ReadAt == null);
    }

    [Fact]
    public async Task Does_not_coalesce_into_a_notification_older_than_the_window()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var tenantAccessor = new CurrentTenantAccessor();
        var userAccessor = new CurrentUserAccessor();
        using (var setup = CreateContext(connection, tenantAccessor)) await setup.Database.EnsureCreatedAsync();

        var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", CreatedAt = DateTimeOffset.UtcNow };
        var actor = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "a@acme.test", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow };
        var recipient = new User { Id = Guid.NewGuid(), TenantId = tenant.Id, Email = "r@acme.test", DisplayName = "R", CreatedAt = DateTimeOffset.UtcNow };
        var doc = new SimplArchive.Domain.Documents.Document { Id = Guid.NewGuid(), TenantId = tenant.Id, Name = "Doc", CreatedByUserId = actor.Id, CreatedAt = DateTimeOffset.UtcNow };
        // An unread comment notification created 7h ago — past the 6h coalesce window.
        var stale = new Notification { Id = Guid.NewGuid(), TenantId = tenant.Id, RecipientUserId = recipient.Id, Type = NotificationType.ChatMessagePosted, Title = "old", Body = "old", DocumentId = doc.Id, EventCount = 1, CreatedAt = DateTimeOffset.UtcNow.AddHours(-7) };
        using (var seed = CreateContext(connection, tenantAccessor)) { seed.Tenants.Add(tenant); seed.Users.AddRange(actor, recipient); seed.Documents.Add(doc); seed.Notifications.Add(stale); await seed.SaveChangesAsync(); }

        tenantAccessor.TenantId = tenant.Id;
        userAccessor.UserId = actor.Id;

        using (var act = CreateContext(connection, tenantAccessor))
        {
            var service = CreateService(act, tenantAccessor, userAccessor);
            await service.NotifyAsync(recipient.Id, NotificationType.ChatMessagePosted, "New comment", "new", doc.Id);
        }

        using var read = CreateContext(connection, tenantAccessor);
        var all = await read.Notifications.Where(n => n.Type == NotificationType.ChatMessagePosted).ToListAsync();
        Assert.Equal(2, all.Count); // the stale one wasn't touched; a fresh one was created
        Assert.Single(all, n => n.EventCount == 1 && n.Title == "New comment");
        Assert.Single(all, n => n.Id == stale.Id && n.EventCount == 1); // unchanged
    }
}

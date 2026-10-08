using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Secrets;

namespace SimplArchive.IntegrationTests;

/// <summary>
/// Notification mail leaves through the RECIPIENT TENANT's own SMTP account when it has one (#1337), not the
/// installation's (#1639).
/// </summary>
/// <remarks>
/// The real sender asks <see cref="TenantSmtpSettingsResolver"/>, which reads the AMBIENT tenant. The dispatcher
/// sends the whole batch from its own scope, where no tenant was set, so every tenant's mail went through the
/// installation's account and the tenant's form was decoration. The sender here is wired the way the real one is,
/// through the resolver, and records which account it got.
/// </remarks>
public class NotificationMailUsesTheTenantsAccountTests
{
    private sealed class AccountRecordingSender(TenantSmtpSettingsResolver accounts) : IEmailSender
    {
        public List<(string To, string? Host)> Sent { get; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
            SendAsync(toAddress, toName, subject, body, [], cancellationToken);

        public async Task SendAsync(string toAddress, string toName, string subject, string body,
            IReadOnlyList<string> envelopeCertificatePems, CancellationToken cancellationToken = default) =>
            Sent.Add((toAddress, (await accounts.ResolveAsync(cancellationToken))?.Host));
    }

    [Fact]
    public async Task Each_tenants_notification_mail_leaves_through_its_own_account_or_the_installations()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var ambient = new CurrentTenantAccessor();
        SimplArchiveDbContext Context() =>
            new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, ambient);
        using (var setup = Context()) await setup.Database.EnsureCreatedAsync();

        var own = new Tenant { Id = Guid.NewGuid(), Name = "Own", CreatedAt = DateTimeOffset.UtcNow, SmtpHost = "smtp.own.example", SmtpPort = 587, SmtpFromAddress = "noreply@own.example" };
        var plain = new Tenant { Id = Guid.NewGuid(), Name = "Plain", CreatedAt = DateTimeOffset.UtcNow };
        var ownUser = new User { Id = Guid.NewGuid(), TenantId = own.Id, Email = "a@own.example", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow };
        var plainUser = new User { Id = Guid.NewGuid(), TenantId = plain.Id, Email = "b@plain.example", DisplayName = "B", CreatedAt = DateTimeOffset.UtcNow };
        Notification Pending(Tenant t, User u) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = t.Id,
            RecipientUserId = u.Id,
            Type = NotificationType.ReviewAssigned,
            Title = "Review requested",
            Body = "body",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        using (var seed = Context())
        {
            seed.Tenants.AddRange(own, plain);
            seed.Users.AddRange(ownUser, plainUser);
            seed.Notifications.AddRange(Pending(own, ownUser), Pending(plain, plainUser));
            await seed.SaveChangesAsync();
        }

        using var act = Context();
        var resolver = new TenantSmtpSettingsResolver(act, ambient, new NullTransitEncryptor(),
            Options.Create(new SmtpOptions { Host = "smtp.installation.example" }), NullLogger<TenantSmtpSettingsResolver>.Instance);
        var sender = new AccountRecordingSender(resolver);
        var dispatcher = new EmailNotificationDispatcher(act, sender, NullLogger<EmailNotificationDispatcher>.Instance,
            NoOpAuditRecorder.Instance, new NoModuleScopeFactory(act), ambient);

        Assert.Equal(2, await dispatcher.DispatchPendingAsync());

        Assert.Contains(("a@own.example", "smtp.own.example"), sender.Sent);
        Assert.Contains(("b@plain.example", "smtp.installation.example"), sender.Sent);
        Assert.Null(ambient.TenantId); // the sweep leaves no tenant behind for whatever runs next in the scope
    }
}

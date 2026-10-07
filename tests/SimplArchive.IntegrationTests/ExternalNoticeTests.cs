using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Secrets;

namespace SimplArchive.IntegrationTests;

// ABI 1.10 (ADR 0902): a module's notice to an address that is NOT a user. Refused unless declared and unless the
// tenant can send mail; when queued, it carries the tenant's name, a reply-to and an attachment frozen to the document's
// version at queue time; the dispatcher claims it, reads the attachment through the storage seam and mails it.
public sealed class ExternalNoticeTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _documentId = Guid.NewGuid();
    private readonly InMemoryObjectStorage _storage = new();
    private readonly CapturingSender _sender = new();
    private const string ObjectKey = "tenants/t/2026/g/content.json";

    public ExternalNoticeTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private sealed class CapturingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The external path must use the EmailMessage overload.");

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }

    private SimplArchiveDbContext Context(CurrentTenantAccessor tenant) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(_connection).Options, tenant);

    private async Task SeedAsync()
    {
        await using var setup = Context(new CurrentTenantAccessor());
        await setup.Database.EnsureCreatedAsync();
        setup.Tenants.Add(new Tenant { Id = _tenantId, Name = "Vendor", CreatedAt = DateTimeOffset.UtcNow });
        setup.Users.Add(new User { Id = _userId, TenantId = _tenantId, Email = "sales@vendor.test", DisplayName = "Sales", CreatedAt = DateTimeOffset.UtcNow });
        setup.Documents.Add(new Document { Id = _documentId, TenantId = _tenantId, Name = "Licence ACME", CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = _userId });
        setup.DocumentVersions.Add(new DocumentVersion
        {
            Id = Guid.NewGuid(),
            TenantId = _tenantId,
            DocumentId = _documentId,
            Status = DocumentVersionStatus.Confirmed,
            ObjectKey = ObjectKey,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = _userId,
            VersionNumber = 1,
            Sha256Hash = new string('a', 64),
        });
        await setup.SaveChangesAsync();
        _storage.Objects[ObjectKey] = "{\"licence\":1}"u8.ToArray();
    }

    private ModuleNoticeDelivery Delivery(SimplArchiveDbContext db, CurrentTenantAccessor tenant, string? smtpHost) =>
        new(db, new NotificationService(db, tenant, new CurrentUserAccessor(), NullLogger<NotificationService>.Instance),
            new TenantSmtpSettingsResolver(db, tenant, new NullTransitEncryptor(), Options.Create(new SmtpOptions { Host = smtpHost }),
                NullLogger<TenantSmtpSettingsResolver>.Instance),
            tenant, NullLogger<ModuleNoticeDelivery>.Instance);

    private Task<bool> DeliverAsync(SimplArchiveDbContext db, CurrentTenantAccessor tenant, string recipient, bool external, string? smtpHost = "smtp.test") =>
        Delivery(db, tenant, smtpHost).DeliverAsync("licensing", recipient, "Your licence", "Renewed until 2027-12-31.",
            "sales@vendor.test", _documentId, aboutDocumentId: null, external, CancellationToken.None);

    [Fact]
    public async Task A_user_takes_the_in_app_path_and_nothing_is_queued_for_mail_here()
    {
        await SeedAsync();
        var tenant = new CurrentTenantAccessor { TenantId = _tenantId };
        await using var db = Context(tenant);

        Assert.True(await DeliverAsync(db, tenant, "SALES@vendor.test", external: false));
        Assert.Equal(1, await db.Notifications.CountAsync());
        Assert.Equal(0, await db.ExternalEmailOutbox.CountAsync());
    }

    [Fact]
    public async Task An_undeclared_external_address_is_refused()
    {
        await SeedAsync();
        var tenant = new CurrentTenantAccessor { TenantId = _tenantId };
        await using var db = Context(tenant);

        Assert.False(await DeliverAsync(db, tenant, "ada@acme.example", external: false));
        Assert.Equal(0, await db.ExternalEmailOutbox.CountAsync());
    }

    [Fact]
    public async Task With_no_mail_server_anywhere_an_external_notice_is_refused()
    {
        await SeedAsync();
        var tenant = new CurrentTenantAccessor { TenantId = _tenantId };
        await using var db = Context(tenant);

        Assert.False(await DeliverAsync(db, tenant, "ada@acme.example", external: true, smtpHost: null));
        Assert.Equal(0, await db.ExternalEmailOutbox.CountAsync());
    }

    [Fact]
    public async Task A_declared_external_notice_is_queued_then_mailed_with_its_frozen_attachment()
    {
        await SeedAsync();
        var tenant = new CurrentTenantAccessor { TenantId = _tenantId };
        await using (var db = Context(tenant))
        {
            Assert.True(await DeliverAsync(db, tenant, "ada@acme.example", external: true));
            var row = await db.ExternalEmailOutbox.SingleAsync();
            Assert.Equal("[Vendor] Your licence", row.Subject);
            Assert.Equal("sales@vendor.test", row.ReplyTo);
            Assert.Equal("Licence ACME.json", row.AttachmentFileName);
            Assert.NotNull(row.AttachmentVersionId);
        }

        var services = new ServiceCollection()
            .AddScoped<CurrentTenantAccessor>()
            .AddScoped<ICurrentTenantAccessor>(sp => sp.GetRequiredService<CurrentTenantAccessor>())
            .AddScoped(sp => Context(sp.GetRequiredService<CurrentTenantAccessor>()))
            .AddSingleton<IObjectStorageClient>(_storage)
            .AddSingleton<IEmailSender>(_sender)
            .AddSingleton<IAuditRecorder>(NoOpAuditRecorder.Instance)
            .BuildServiceProvider();
        await using (var db = Context(new CurrentTenantAccessor()))
        {
            var dispatcher = new ExternalEmailDispatcher(db, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExternalEmailDispatcher>.Instance);
            Assert.Equal(1, await dispatcher.DispatchPendingAsync(CancellationToken.None));
            Assert.Equal(0, await db.ExternalEmailOutbox.CountAsync());   // a sent row is deleted
            Assert.Equal(0, await dispatcher.DispatchPendingAsync(CancellationToken.None));
        }

        var mail = Assert.Single(_sender.Sent);
        Assert.Equal(("ada@acme.example", "[Vendor] Your licence", "sales@vendor.test", "Licence ACME.json"),
            (mail.ToAddress, mail.Subject, mail.ReplyTo, mail.AttachmentFileName));
        Assert.Equal("{\"licence\":1}"u8.ToArray(), mail.AttachmentContent);
    }
}

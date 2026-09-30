using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Modules;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Modules;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.IntegrationTests;

/// <summary>
/// Notification mail asks the module which certificates a reader holds (ADR 0856).
/// </summary>
/// <remarks>
/// The dispatcher resolved column-then-registry and never asked a module, so a reader whose only
/// certificate is enrolled in one got PLAINTEXT notification mail — the same defect ADR 0855 fixed for IMAP.
/// These assert the three answers a module can give and what each does to the other sources.
/// </remarks>
public class NotificationsAskTheModuleTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    [Fact]
    public async Task A_modules_certificates_are_what_the_notification_is_addressed_to()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection, columnPem: "column-pem", activateModule: true);

        var sent = await DispatchAsync(connection, new EnrollingModule("card-pem", "laptop-pem"));

        // BOTH of the module's, and NOT the column's — where a module answers it is the only source.
        Assert.Equal(["card-pem", "laptop-pem"], sent);
    }

    [Fact]
    public async Task A_module_answering_NONE_sends_plaintext_rather_than_falling_back()
    {
        // The module is active and says this reader holds no certificate. The column has one; it must NOT
        // be used, or a certificate the module revoked would go on opening the reader's mail.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection, columnPem: "column-pem", activateModule: true);

        var sent = await DispatchAsync(connection, new EnrollingModule());

        Assert.Empty(sent);
    }

    [Fact]
    public async Task With_no_module_the_column_still_answers()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection, columnPem: "column-pem", activateModule: false);

        var sent = await DispatchAsync(connection, module: null);

        Assert.Equal(["column-pem"], sent);
    }

    [Fact]
    public async Task A_module_mounted_but_NOT_LICENSED_sends_plaintext_and_that_is_a_downgrade()
    {
        // Worth pinning because it is the surprising case, and it is a DOWNGRADE on a fail-open path: a
        // declared-but-unactivated module answers EMPTY (ModuleReaderCertificatesTests says so), empty
        // closes every other source, and notification mail therefore goes plaintext where the column would
        // previously have sealed it. Accepted rather than worked around — the alternative is consulting the
        // column anyway, which is the union ADR 0842 forbids and which breaks revocation. The operator's fix
        // is to license the module; ADR 0740's grace ladder is what warns them first.
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await SeedAsync(connection, columnPem: "column-pem", activateModule: false);

        var sent = await DispatchAsync(connection, new EnrollingModule("card-pem"));

        Assert.Empty(sent);
    }

    private static async Task SeedAsync(SqliteConnection connection, string? columnPem, bool activateModule)
    {
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();

        db.Tenants.Add(new Tenant { Id = TenantId, Name = "Acme", Status = TenantStatus.Active, CreatedAt = DateTimeOffset.UtcNow });
        db.Users.Add(new User
        {
            Id = UserId,
            TenantId = TenantId,
            Email = "reader@acme.test",
            DisplayName = "Reader",
            IsActive = true,
            SmimeCertificatePem = columnPem,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        if (activateModule)
        {
            db.ModuleActivations.Add(new ModuleActivation
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                ModuleId = "encryption",
                SupportContractEndDate = DateTimeOffset.UtcNow.AddYears(1),
                LicenseDocumentId = Guid.NewGuid(),
                ActivatedAt = DateTimeOffset.UtcNow,
            });
        }

        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId,
            RecipientUserId = UserId,
            Type = NotificationType.ReviewAssigned,
            Title = "Approve Salary review 2026",
            Body = "by Friday",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>Dispatches the one pending notification and returns the certificates it was addressed to.</summary>
    private static async Task<IReadOnlyList<string>> DispatchAsync(SqliteConnection connection, IIndustryModule? module)
    {
        await using var db = Context(connection);
        var sender = new CapturingSender();

        await new SimplArchive.Infrastructure.Notifications.EmailNotificationDispatcher(
            db, sender, InertEnvelopeClient(), NullLogger<SimplArchive.Infrastructure.Notifications.EmailNotificationDispatcher>.Instance,
            NoOpAuditRecorder.Instance, new ModuleScopeFactory(connection, module)).DispatchPendingAsync();

        return sender.Certificates;
    }

    private static SimplArchiveDbContext Context(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, new CurrentTenantAccessor());

    /// <summary>No Encryption:ServiceUrl, so the registry answers nothing — the column/module is the story.</summary>
    private static SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient InertEnvelopeClient() =>
        new(new InertHttpClientFactory(), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            NullLogger<SimplArchive.Infrastructure.Encryption.MessageEnvelopeClient>.Instance);

    private sealed class InertHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class CapturingSender : IEmailSender
    {
        public IReadOnlyList<string> Certificates { get; private set; } = [];

        public Task SendAsync(string toAddress, string toName, string subject, string body, CancellationToken cancellationToken = default) =>
            SendAsync(toAddress, toName, subject, body, envelopeCertificatePems: [], cancellationToken);

        public Task SendAsync(string toAddress, string toName, string subject, string body,
            IReadOnlyList<string> envelopeCertificatePems, CancellationToken cancellationToken = default)
        {
            Certificates = envelopeCertificatePems;
            return Task.CompletedTask;
        }
    }

    /// <summary>A module that answers the capability with whatever it was given.</summary>
    private sealed class EnrollingModule(params string[] pems) : IIndustryModule
    {
        public Func<ReaderCertificateContext, Task<IReadOnlyList<ReaderCertificate>>>? ReaderCertificates =>
            _ => Task.FromResult<IReadOnlyList<ReaderCertificate>>(
                [.. pems.Select(pem => new ReaderCertificate(pem, "device", DateTimeOffset.MaxValue))]);

        public string ModuleId => "encryption";
        public string DisplayName => "Encryption";
        public int AbiMajorVersion => ModuleAbiVersion.Major;
        public int AbiMinorVersion => ModuleAbiVersion.Minor;
        public string LicenseVerifyKeyPem => string.Empty;
        public IReadOnlyList<ModuleMaskSeed> Masks => [];
        public void ConfigureServices(IServiceCollection services) { }
    }

    /// <summary>
    /// The per-item scope the dispatcher creates, reduced to what that seam resolves.
    /// </summary>
    /// <remarks>
    /// A fresh <see cref="CurrentTenantAccessor"/> per call, like the real scope: the dispatcher sets the
    /// tenant on it per recipient, because the sweep itself spans every tenant and has no ambient one.
    /// </remarks>
    private sealed class ModuleScopeFactory(SqliteConnection connection, IIndustryModule? module)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        // ONE accessor and ONE DbContext built FROM it, per scope — which is the whole point of mirroring
        // the real boundary here rather than handing over the dispatcher's own context. The dispatcher sets
        // the tenant on the scope's accessor; the scope's DbContext must be the one whose query filter
        // reads that same accessor, or the tenant-scoped activation lookup finds nothing and every module
        // looks unlicensed. The first version of this fake shared the dispatcher's context (built with a
        // different accessor), and the "module answers certificates" case failed with an empty answer that
        // looked exactly like a code defect.
        private readonly CurrentTenantAccessor _tenant = new();
        private SimplArchiveDbContext? _scoped;

        private SimplArchiveDbContext Scoped => _scoped ??= new SimplArchiveDbContext(
            new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options, _tenant);

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType) => serviceType switch
        {
            _ when serviceType == typeof(ICurrentTenantAccessor) => _tenant,
            _ when serviceType == typeof(SimplArchiveDbContext) => Scoped,
            _ when serviceType == typeof(ModuleReaderCertificates) =>
                new ModuleReaderCertificates(Scoped, this, NullLogger<ModuleReaderCertificates>.Instance),
            _ when serviceType == typeof(IReadOnlyList<ModuleLoader.LoadedModule>) =>
                module is null ? Array.Empty<ModuleLoader.LoadedModule>() : new[] { new ModuleLoader.LoadedModule(module, "test://enc") },
            _ => null,
        };

        public void Dispose() => _scoped?.Dispose();
    }
}

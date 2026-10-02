using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.Infrastructure.Secrets;

namespace SimplArchive.IntegrationTests;

// Which SMTP account a tenant's mail leaves through (#1337).
//
// The normal deployment runs no relay of its own: it authenticates against an ordinary provider's submission
// server, and that account is per tenant because each tenant's mail should come from the tenant's own domain.
// These pin the three rules that decide what actually goes on the wire.
public class TenantSmtpSettingsResolverTests
{
    private sealed class FailingEncryptor : ITransitEncryptor
    {
        public Task<string> EncryptAsync(string plaintext, CancellationToken cancellationToken = default) =>
            Task.FromResult(plaintext);

        public Task<string> DecryptAsync(string ciphertext, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the secrets service is unreachable");
    }

    private static readonly SmtpOptions Installation = new()
    {
        Host = "smtp.installation.test",
        Port = 25,
        User = "installation-user",
        Password = "installation-password",
        FromAddress = "notifications@installation.test",
        FromName = "The Installation",
    };

    private static SimplArchiveDbContext Context(SqliteConnection connection, Guid? tenantId = null) =>
        new(new DbContextOptionsBuilder<SimplArchiveDbContext>().UseSqlite(connection).Options,
            new CurrentTenantAccessor { TenantId = tenantId });

    private static TenantSmtpSettingsResolver Resolver(
        SimplArchiveDbContext db, Guid? tenantId, ITransitEncryptor? transit = null, SmtpOptions? options = null) =>
        new(db, new CurrentTenantAccessor { TenantId = tenantId }, transit ?? new NullTransitEncryptor(),
            Options.Create(options ?? Installation), NullLogger<TenantSmtpSettingsResolver>.Instance);

    private static async Task<(SqliteConnection Connection, Guid TenantId)> SeedAsync(Action<Tenant>? configure = null)
    {
        var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        await using (var seed = Context(connection))
        {
            await seed.Database.EnsureCreatedAsync();
            var tenant = new Tenant { Id = Guid.NewGuid(), Name = "Acme", Status = TenantStatus.Active };
            configure?.Invoke(tenant);
            seed.Tenants.Add(tenant);
            await seed.SaveChangesAsync();
            return (connection, tenant.Id);
        }
    }

    [Fact]
    public async Task A_tenant_with_no_host_uses_the_installations_account()
    {
        // The property that makes this additive: every deployment that has a relay keeps behaving exactly as
        // before until somebody fills the tenant form in.
        var (connection, tenantId) = await SeedAsync();
        await using var db = Context(connection, tenantId);

        var account = await Resolver(db, tenantId).ResolveAsync();

        Assert.NotNull(account);
        Assert.Equal("smtp.installation.test", account!.Host);
        Assert.Equal("notifications@installation.test", account.FromAddress);
    }

    [Fact]
    public async Task A_tenant_with_a_host_uses_its_own_account_entirely()
    {
        // ALL-OR-NOTHING, keyed on the host. The credentials must come from the same account as the host —
        // a tenant's server reached with the installation's password is a configuration nobody chose, and it
        // fails at 03:00 as an authentication error nobody can place.
        var (connection, tenantId) = await SeedAsync(t =>
        {
            t.SmtpHost = "smtp.acme.test";
            t.SmtpPort = 465;
            t.SmtpUseStartTls = true;
            t.SmtpUser = "acme-user";
            t.SmtpPassword = "acme-password";
            t.SmtpFromAddress = "notifications@acme.test";
            t.SmtpFromName = "Acme";
        });

        await using var db = Context(connection, tenantId);
        var account = await Resolver(db, tenantId).ResolveAsync();

        Assert.NotNull(account);
        Assert.Equal("smtp.acme.test", account!.Host);
        Assert.Equal(465, account.Port);
        Assert.True(account.UseStartTls);
        Assert.Equal("acme-user", account.User);
        Assert.Equal("acme-password", account.Password);
        Assert.Equal("notifications@acme.test", account.FromAddress);

        // Nothing of the installation's leaked into it — the assertion that would catch a per-field fallback
        // being reintroduced, which is the shape this rule exists to forbid.
        Assert.DoesNotContain("installation", account.User);
        Assert.DoesNotContain("installation", account.Password);
    }

    [Fact]
    public async Task An_undecryptable_password_refuses_rather_than_falling_back()
    {
        // The important one. Falling back here would deliver this tenant's mail from the WRONG SENDER through
        // an account they never chose — and it would arrive, so nobody would notice. Refusing keeps the message
        // in the outbox for the retry path, and the queue drains when the secrets service returns.
        var (connection, tenantId) = await SeedAsync(t =>
        {
            t.SmtpHost = "smtp.acme.test";
            t.SmtpPassword = "cannot-be-decrypted";
        });

        await using var db = Context(connection, tenantId);
        var resolver = Resolver(db, tenantId, new FailingEncryptor());

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync());
        Assert.Contains(tenantId.ToString(), refusal.Message);
    }

    [Fact]
    public async Task With_no_account_anywhere_there_is_nothing_to_send_through()
    {
        // The sender turns this null into a refusal, so the message stays QUEUED rather than being dropped
        // (owner-decided): a notification somebody was asked to act on must not vanish with an Information
        // line as its only trace.
        var (connection, tenantId) = await SeedAsync();
        await using var db = Context(connection, tenantId);

        var account = await Resolver(db, tenantId, options: new SmtpOptions { Host = null }).ResolveAsync();

        Assert.Null(account);
    }
}

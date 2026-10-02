using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Infrastructure.Notifications;

/// <summary>The account a message is actually submitted through — a tenant's own, or the installation's.</summary>
public sealed record EffectiveSmtpSettings(
    string Host, int Port, bool UseStartTls, string? User, string? Password, string FromAddress, string FromName)
{
    /// <summary>Whose account this is, for a log line that has to name it.</summary>
    public required string Source { get; init; }
}

/// <summary>
/// Answers which SMTP account the AMBIENT tenant's mail leaves through (#1337).
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved from the ambient tenant rather than passed as a parameter</b>, because every caller already
/// establishes one: the outbox dispatcher sets <see cref="ICurrentTenantAccessor"/> per message before sending,
/// and an administrator's test delivery runs inside their own request. Threading a settings object through
/// <c>IEmailSender</c> instead would have changed the signature every caller uses to say something all of them
/// already know.
/// </para>
/// <para>
/// <b>A tenant with no host falls back to the installation's options</b>, which is what makes this additive:
/// an existing deployment behaves exactly as before until somebody fills the form in.
/// </para>
/// </remarks>
public sealed class TenantSmtpSettingsResolver(
    SimplArchiveDbContext dbContext,
    ICurrentTenantAccessor tenant,
    ITransitEncryptor transit,
    IOptions<SmtpOptions> global,
    ILogger<TenantSmtpSettingsResolver> logger)
{
    /// <summary>The installation's own account, used when the tenant brings none.</summary>
    private EffectiveSmtpSettings? Global =>
        global.Value.Host is { Length: > 0 } host
            ? new EffectiveSmtpSettings(host, global.Value.Port, global.Value.UseStartTls, global.Value.User,
                global.Value.Password, global.Value.FromAddress, global.Value.FromName)
            { Source = "the installation" }
            : null;

    public async Task<EffectiveSmtpSettings?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return Global;
        }

        // IgnoreQueryFilters on the tenant filter: this runs inside the dispatcher's own scope, which sets the
        // ambient tenant precisely so the filter would match — but the test-delivery path reads it for the
        // tenant the administrator belongs to, and asking by id is what both have in common.
        var row = await dbContext.Tenants
            .IgnoreQueryFilters(["TenantFilter"])
            .Where(t => t.Id == tenantId)
            .Select(t => new
            {
                t.SmtpHost,
                t.SmtpPort,
                t.SmtpUseStartTls,
                t.SmtpUser,
                t.SmtpPassword,
                t.SmtpFromAddress,
                t.SmtpFromName,
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row?.SmtpHost is not { Length: > 0 } tenantHost)
        {
            return Global;
        }

        // DECRYPTED HERE, and a failure is NOT a fallback. Falling back to the installation's account would
        // deliver this tenant's mail from the wrong sender through an account they never chose — a disclosure
        // nobody would notice, because the mail arrives. So it throws, the outbox keeps the row, the existing
        // retry path carries it, and OpenBao coming back drains the queue (owner-decided).
        string? password = null;
        if (row.SmtpPassword is { Length: > 0 } stored)
        {
            try
            {
                password = await transit.DecryptAsync(stored, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Tenant {TenantId}'s SMTP password could not be decrypted, so its mail cannot be submitted "
                    + "through its own account. Nothing was sent through the installation's account instead — "
                    + "that would send this tenant's mail from the wrong sender. The message stays queued and "
                    + "will be retried; check that the secrets service is reachable.",
                    tenantId);

                throw new InvalidOperationException(
                    $"The SMTP password for tenant {tenantId} could not be decrypted.", exception);
            }
        }

        return new EffectiveSmtpSettings(
            tenantHost,
            row.SmtpPort,
            row.SmtpUseStartTls,
            row.SmtpUser,
            password,
            // The from-address falls back WITHIN the tenant's own account rather than to the installation's:
            // a tenant that set a host but left the address blank still sends through its own server, and the
            // global address is the only sensible label left. It is not a cross-account mix — the submission
            // account is wholly the tenant's either way.
            row.SmtpFromAddress is { Length: > 0 } from ? from : global.Value.FromAddress,
            row.SmtpFromName is { Length: > 0 } name ? name : global.Value.FromName)
        {
            Source = $"tenant {tenantId}",
        };
    }
}

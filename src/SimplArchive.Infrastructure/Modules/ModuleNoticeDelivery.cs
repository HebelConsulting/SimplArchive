using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Infrastructure.Notifications;
using SimplArchive.Infrastructure.Persistence;
using SimplArchive.ModuleAbi;

namespace SimplArchive.Infrastructure.Modules;

/// <summary>
/// Where a module's notice goes (ABI 1.10, ADR 0902), one rule for the escalation sweep and for an action's
/// <see cref="IModuleNotices"/>: a user of the tenant gets an in-app notification (and mail, as their settings say); any
/// other address is queued in <see cref="ExternalEmailOutbox"/> only when the module declared it may reach one and the
/// tenant can send mail. Everything is written in the CURRENT transaction, so a rolled-back act sends nothing.
/// </summary>
/// <remarks>A refusal is a Warning naming the module and the address and saying why: the module only learns
/// <c>false</c>, and nobody else would find out (ADR 0626). Never the message body.</remarks>
public sealed class ModuleNoticeDelivery(
    SimplArchiveDbContext dbContext, INotificationService notifications, TenantSmtpSettingsResolver smtp,
    ICurrentTenantAccessor tenant, ILogger<ModuleNoticeDelivery> logger)
{
    public async Task<bool> DeliverAsync(
        string moduleId, string recipient, string subject, string message, string? replyTo, Guid? attachmentDocumentId,
        Guid? aboutDocumentId, bool externalAllowed, CancellationToken cancellationToken)
    {
        var normalized = recipient.Trim().ToUpperInvariant();
        var userId = await dbContext.Users.Where(u => u.NormalizedEmail == normalized).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        if (userId is { } user)
        {
            await notifications.NotifyAsync(user, NotificationType.ModuleStatusEscalation, subject, message, aboutDocumentId, cancellationToken);
            return true;
        }

        if (!externalAllowed)
        {
            logger.LogWarning(
                "Module {ModuleId} addressed {Recipient}, who is not a user of this tenant, without declaring external recipients; "
                + "the notice was not delivered.", moduleId, recipient);
            return false;
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("A module notice was delivered with no tenant.");
        if (await smtp.ResolveAsync(cancellationToken) is null)
        {
            logger.LogWarning(
                "Module {ModuleId} could not mail {Recipient}: neither the tenant nor the installation has a mail server configured.",
                moduleId, recipient);
            return false;
        }

        var (attachmentVersionId, attachmentFileName) = attachmentDocumentId is { } documentId
            ? await FreezeAttachmentAsync(documentId, cancellationToken)
            : (null, null);
        var tenantName = await dbContext.Tenants.IgnoreQueryFilters().Where(t => t.Id == tenantId).Select(t => t.Name).SingleAsync(cancellationToken);

        dbContext.ExternalEmailOutbox.Add(new ExternalEmailOutbox
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ModuleId = moduleId,
            RecipientAddress = recipient.Trim(),
            Subject = Truncate($"[{tenantName}] {subject}", 300),
            Body = message,
            ReplyTo = string.IsNullOrWhiteSpace(replyTo) ? null : replyTo.Trim(),
            AttachmentVersionId = attachmentVersionId,
            AttachmentFileName = attachmentFileName,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogTrace("Module {ModuleId} queued mail to {Recipient}, reply-to {ReplyTo}, attachment {Attachment}",
            moduleId, recipient, replyTo, attachmentFileName);
        return true;
    }

    /// <summary>The document's CURRENT version and a file name for it, fixed now so the mail carries what was meant.</summary>
    private async Task<(Guid? VersionId, string? FileName)> FreezeAttachmentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        var document = await dbContext.Documents.Where(d => d.Id == documentId)
            .Select(d => new { d.Name, d.CurrentVersionId }).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ArgumentException($"The attachment document {documentId} does not exist.", nameof(documentId));
        var version = await CurrentVersion.ResolveAsync(dbContext.DocumentVersions, documentId, document.CurrentVersionId, cancellationToken)
            ?? throw new ArgumentException($"The attachment document {documentId} has no content.", nameof(documentId));
        var extension = Path.GetExtension(version.ObjectKey);
        var name = document.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? document.Name : $"{document.Name}{extension}";
        return (version.Id, Truncate(name, 255));
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];
}

/// <summary>
/// The host's <see cref="IModuleNotices"/> (ABI 1.10): the acting module's notice, external only when the module declares
/// <see cref="IIndustryModule.SendsExternalNotices"/>.
/// </summary>
public sealed class ModuleNotices(
    ModuleNoticeDelivery delivery, ModuleIdentityAccessor identity, IReadOnlyList<ModuleLoader.LoadedModule> modules) : IModuleNotices
{
    public Task<bool> QueueAsync(ModuleNotice notice, CancellationToken cancellationToken = default)
    {
        var moduleId = identity.ModuleId
            ?? throw new InvalidOperationException("A module notice was queued with no module acting; the host sets the identity before module code runs.");
        var external = modules.FirstOrDefault(m => m.Module.ModuleId == moduleId)?.Module.SendsExternalNotices ?? false;
        return delivery.DeliverAsync(moduleId, notice.RecipientEmail, notice.Subject, notice.Message, notice.ReplyTo,
            notice.AttachmentDocumentId, aboutDocumentId: null, external, cancellationToken);
    }
}

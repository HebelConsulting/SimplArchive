using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Lmtp;

/// <summary>Turns an accepted envelope recipient into a filed message (ADR 0628).</summary>
/// <remarks>
/// The delivery contract is the whole of the semantics, and the ORDER is the contract: object written, row
/// committed, <b>then</b> the reply. The MTA discards whatever we acknowledge, so a <c>250</c> sent before the
/// bytes are durable is the one unrecoverable failure here — everything else defers.
/// </remarks>
public class LmtpDelivery
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _tenantAccessor;
    private readonly IObjectStorageClient _storage;
    private readonly DocumentFinalizer _finalizer;
    private readonly PersonalRepositoryProvisioner _personalSpace;
    private readonly ILogger<LmtpDelivery> _logger;

    public LmtpDelivery(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor tenantAccessor,
        IObjectStorageClient storage,
        DocumentFinalizer finalizer,
        PersonalRepositoryProvisioner personalSpace,
        ILogger<LmtpDelivery> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _storage = storage;
        _finalizer = finalizer;
        _personalSpace = personalSpace;
        _logger = logger;
    }

    /// <summary>The tenant and user an envelope recipient names, or null if nobody here answers to it.</summary>
    public async Task<(Guid TenantId, Guid UserId)?> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return null;
        }

        var localPart = address[..at];
        var domain = address[(at + 1)..].Trim().TrimEnd('.').ToUpperInvariant();

        // Both lookups run BEFORE a tenant is known, so both must ignore the tenant filter — left on, its
        // TenantId == null predicate matches zero rows and every recipient resolves as unknown. This is the
        // same rule the login and client-id lookups follow.
        var tenantId = await _dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.NormalizedDomain == domain)
            .Select(d => (Guid?)d.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        if (tenantId is not { } tenant)
        {
            return null;
        }

        // The local part identifies the USER, matched on the same normalized-email key the rest of the
        // codebase uses — a mail local part is case-insensitive, so the raw column cannot be the key.
        var normalizedEmail = $"{localPart}@{domain}".ToUpperInvariant();
        var userId = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.TenantId == tenant && u.NormalizedEmail == normalizedEmail && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return userId is { } user ? (tenant, user) : null;
    }

    /// <summary>Files the message and returns the LMTP reply line for THIS recipient.</summary>
    public async Task<string> DeliverAsync(string address, string sender, byte[] payload, CancellationToken cancellationToken)
    {
        if (await ResolveAsync(address, cancellationToken) is not { } resolved)
        {
            // It resolved at RCPT and does not now — the account was removed mid-transaction. Permanent, so
            // the sender is told rather than the MTA retrying against a user who no longer exists.
            _logger.LogWarning("LMTP: {Address} resolved at RCPT but not at delivery; refusing permanently", address);
            return "550 no such recipient here";
        }

        var (tenantId, userId) = resolved;

        try
        {
            // The DbContext reads the tenant from this accessor, and there is no request to have set it — the
            // MTA is the caller. Setting it here is what puts every query below inside the right tenant.
            ((CurrentTenantAccessor)_tenantAccessor).TenantId = tenantId;

            var inboxId = await EnsureInboxAsync(tenantId, userId, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var versionId = Guid.NewGuid();
            var storageFolderId = Guid.NewGuid();
            var objectKey = ObjectKeyBuilder.Build(tenantId, now, storageFolderId, versionId, ".eml");

            // Object FIRST. A row pointing at bytes that are not there is a document that opens to an error;
            // bytes with no row are an orphan a sweep can find. Only one of those is recoverable.
            await _storage.PutObjectAsync(objectKey, new MemoryStream(payload), "message/rfc822");

            var document = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = inboxId,
                Name = await UniqueNameAsync(inboxId, SubjectOf(payload), cancellationToken),
                CreatedByUserId = userId,
                CreatedAt = now,
                StorageFolderId = storageFolderId,
            };
            _dbContext.Documents.Add(document);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Pending + the shared finalizer, never a hand-written Confirmed version: the status is guarded by
            // a CHECK constraint, and the finalizer is also what indexes the message and extracts its headers.
            var version = new DocumentVersion
            {
                Id = versionId,
                DocumentId = document.Id,
                TenantId = tenantId,
                Status = DocumentVersionStatus.Pending,
                ObjectKey = objectKey,
                CreatedByUserId = userId,
                CreatedAt = now,
                DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
            };
            _dbContext.DocumentVersions.Add(version);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await _finalizer.FinalizeAsync(version, cancellationToken);

            _logger.LogInformation(
                "Delivered a message from {Sender} to {Address} as document {DocumentId} ({Bytes} bytes)",
                sender, address, document.Id, payload.Length);

            // Only now. Everything above is durable.
            return "250 delivered";
        }
        catch (Exception e)
        {
            // 4xx, deliberately: the MTA holds the mail and retries, so our being broken DEFERS rather than
            // loses. A 5xx here would bounce a message that has nothing wrong with it.
            _logger.LogError(e,
                "LMTP: deferring {Address} after a delivery failure — the MTA retains the mail and will retry",
                address);
            return "451 temporary failure storing the message";
        }
    }

    /// <summary>The user's ephemeral inbox, created on first delivery.</summary>
    /// <remarks>
    /// Lazily rather than eagerly: a mailbox provisioned for every user would put an empty "My eMails" in the
    /// personal space of everyone who never receives mail. Creating it when the first message arrives means
    /// the node exists exactly when it has something to hold, and the Mailbox cardinality rule (ADR 0629)
    /// guarantees a personal space never ends up with two.
    /// </remarks>
    private async Task<Guid> EnsureInboxAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        var personal = await _personalSpace.EnsureAsync(userId, tenantId, cancellationToken);

        var mailboxVersionIds = await _dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenantId && v.MaskId == WellKnownMaskIds.Mailbox)
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var mailboxId = await _dbContext.Documents
            .Where(d => d.ParentId == personal.Id && d.MaskVersionId != null && mailboxVersionIds.Contains(d.MaskVersionId.Value))
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (mailboxId is null)
        {
            var mailbox = new Document
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ParentId = personal.Id,
                Name = "My eMails",
                MaskVersionId = mailboxVersionIds.FirstOrDefault(),
                CreatedByUserId = userId,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            _dbContext.Documents.Add(mailbox);
            await _dbContext.SaveChangesAsync(cancellationToken);
            mailboxId = mailbox.Id;
        }

        var inboxId = await _dbContext.Documents
            .Where(d => d.ParentId == mailboxId && d.Name == "INBOX")
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (inboxId is { } existing)
        {
            return existing;
        }

        var inbox = new Document
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            ParentId = mailboxId.Value,
            Name = "INBOX",
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.Documents.Add(inbox);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return inbox.Id;
    }

    /// <summary>The message's Subject, or a stand-in — the document's name, as an appended message gets.</summary>
    private static string SubjectOf(byte[] payload)
    {
        // Headers only, and only the first 8 KB of them: the body is not being parsed here, and a message with
        // no recognisable Subject must still be filed rather than refused.
        var text = System.Text.Encoding.UTF8.GetString(payload, 0, Math.Min(payload.Length, 8192));
        foreach (var line in text.Split("\r\n"))
        {
            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
            {
                var subject = line["Subject:".Length..].Trim();
                if (subject.Length > 0)
                {
                    // '/' is the hierarchy delimiter on the IMAP wire and in WebDAV paths, so a subject
                    // carrying one would address the wrong thing on both surfaces.
                    return subject.Replace('/', '-')[..Math.Min(subject.Length, 200)];
                }
            }
        }

        return "(no subject)";
    }

    /// <summary>
    /// Sibling names are unique, and two messages sharing a subject is ordinary rather than exceptional — so
    /// the name is disambiguated here, exactly as an appended message's is.
    /// </summary>
    private async Task<string> UniqueNameAsync(Guid parentId, string stem, CancellationToken cancellationToken)
    {
        var siblings = await _dbContext.Documents
            .Where(d => d.ParentId == parentId)
            .Select(d => d.Name)
            .ToListAsync(cancellationToken);

        var name = stem;
        for (var i = 2; siblings.Contains(name, StringComparer.OrdinalIgnoreCase); i++)
        {
            name = $"{stem} ({i})";
        }

        return name;
    }
}

using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Masks;
using SimplArchive.Infrastructure.Masks;
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
    private readonly PersonalMailboxProvisioner _mailbox;
    private readonly ILogger<LmtpDelivery> _logger;

    public LmtpDelivery(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor tenantAccessor,
        IObjectStorageClient storage,
        DocumentFinalizer finalizer,
        PersonalMailboxProvisioner mailbox,
        ILogger<LmtpDelivery> logger)
    {
        _dbContext = dbContext;
        _tenantAccessor = tenantAccessor;
        _storage = storage;
        _finalizer = finalizer;
        _mailbox = mailbox;
        _logger = logger;
    }

    /// <summary>
    /// The deliveries an envelope recipient names — empty if nobody here answers to it (#703 PR 3).
    /// </summary>
    /// <remarks>
    /// Two disjoint branches, and their disjointness is enforced upstream: a USER owns the address (their
    /// personal inbox, the original rule, untouched), or one or more MAILBOXES claim it in their
    /// "eMail Addresses" list — a claim on an existing user's address is refused at write time (ADR 0679),
    /// so an address never resolves both ways. Several mailboxes claiming one address is the CONFIRMED
    /// duplicate an admin explicitly asked for: one RCPT, one copy into each — fan-out is a feature.
    /// </remarks>
    /// <summary>
    /// One filing a recipient resolves to. <see cref="MailboxId"/> null = the user's own personal inbox;
    /// set = a department mailbox's lazily-created Inbox (#703 PR 4). <see cref="UserId"/> is the
    /// ATTRIBUTION principal for the personal branch, and null for a department mailbox created by a
    /// service account.
    /// </summary>
    /// <param name="ServiceAccountId">Attribution when a department mailbox was created by a service
    /// account — a filed message must name exactly one creator, and the mailbox knows which kind it has.</param>
    public sealed record DeliveryTarget(Guid TenantId, Guid? UserId, Guid? MailboxId, Guid? ServiceAccountId = null);

    public async Task<IReadOnlyList<DeliveryTarget>> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        var at = address.LastIndexOf('@');
        if (at <= 0 || at == address.Length - 1)
        {
            return [];
        }

        var localPart = address[..at];
        var domain = address[(at + 1)..].Trim().TrimEnd('.').ToUpperInvariant();

        // Every lookup here runs BEFORE a tenant is known, so each must ignore the tenant filter — left on,
        // its TenantId == null predicate matches zero rows and every recipient resolves as unknown. This is
        // the same rule the login and client-id lookups follow. The queries stay tenant-scoped by the
        // explicit TenantId predicates.
        // VERIFIED domains only (#667). An unverified claim is a statement someone typed, not a fact — and
        // accepting mail on it would let a tenant receive another organisation's mail by claiming its domain
        // first. The MTA's own virtual-domain query carries the same condition, so an unverified recipient is
        // refused at RCPT rather than accepted and bounced afterwards.
        //
        // The two rejections are read APART, deliberately. "No such domain" and "the domain is registered but
        // unverified" produce the same 550 to the sender and are the same empty result here — and the second
        // one is an administrator's unfinished task, visible to nobody: the person who added the domain sees a
        // list entry, the sender sees a bounce, and the logs of a healthy and a half-configured install are
        // byte-identical. That is exactly the shape ADR 0626 exists to forbid.
        var claim = await _dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
            .Where(d => d.NormalizedDomain == domain)
            .Select(d => new { d.TenantId, d.VerifiedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (claim is null)
        {
            _logger.LogTrace("LMTP: {Address} is not for any registered domain; refusing", address);
            return [];
        }

        if (claim.VerifiedAt is null)
        {
            // Warning, and it names the remedy: this is the one refusal here that an administrator can fix and
            // would otherwise never learn about.
            _logger.LogWarning(
                "LMTP: refusing {Address} — the domain {Domain} is registered but NOT VERIFIED, so no mail is "
                + "accepted for it. Publish its DNS challenge and verify it under the tenant's mail domains. "
                + "Set the LMTP log level to Trace for the full exchange.",
                address, domain);
            return [];
        }

        var tenant = claim.TenantId;

        // The user branch: the local part identifies the USER, matched on the same normalized-email key the
        // rest of the codebase uses — a mail local part is case-insensitive, so the raw column cannot be the
        // key.
        var normalizedEmail = $"{localPart}@{domain}".ToUpperInvariant();
        var userId = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
            .Where(u => u.TenantId == tenant && u.NormalizedEmail == normalizedEmail && u.IsActive)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (userId is { } user)
        {
            return [new DeliveryTarget(tenant, user, null)];
        }

        // The claims branch: every LIVE mailbox whose address list contains the recipient, case-insensitively
        // (the NormalizedEmail precedent). Only the TENANT filter is bypassed — the soft-delete filter stays
        // on the Documents join, which is what makes a recycled mailbox stop receiving at the moment of its
        // deletion (the fact the delete gate's reasoning rests on, ADR 0679).
        var owners = await _dbContext.FieldValues.IgnoreQueryFilters(["TenantFilter"])
            .Where(v => v.TenantId == tenant && v.Value.ToUpper() == normalizedEmail)
            .Join(_dbContext.FieldDefinitions.IgnoreQueryFilters(["TenantFilter"]),
                v => v.FieldDefinitionId, f => f.Id, (v, f) => new { v.DocumentId, f.Name, f.MaskVersionId })
            .Where(x => x.Name == WellKnownMaskSeeder.MailboxAddressesFieldName)
            .Join(_dbContext.MaskVersions.IgnoreQueryFilters(["TenantFilter"]),
                x => x.MaskVersionId, mv => mv.Id, (x, mv) => new { x.DocumentId, mv.MaskId })
            .Where(x => x.MaskId == WellKnownMaskIds.Mailbox)
            .Join(_dbContext.Documents.IgnoreQueryFilters(["TenantFilter"]),
                x => x.DocumentId, d => d.Id, (_, d) => new { d.Id, d.PersonalRootOwnerId, d.CreatedByUserId, d.CreatedByServiceAccountId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var targets = new List<DeliveryTarget>();
        foreach (var mailbox in owners)
        {
            // A mailbox with no personal-root owner is a DEPARTMENT mailbox (#703 PR 4): it delivers into
            // its own lazily-created Inbox, and deliberately with NO active-owner check — a department box
            // outlives its creator, and mail to sales@ must not stop because whoever clicked "New mailbox"
            // later left the company.
            if (mailbox.PersonalRootOwnerId is not { } ownerId)
            {
                targets.Add(new DeliveryTarget(tenant, mailbox.CreatedByUserId, mailbox.Id, mailbox.CreatedByServiceAccountId));
                continue;
            }

            // The owner must still be able to RECEIVE: a deactivated user's mailbox not accepting mail is the
            // user branch's own rule, and a claim must not become the way around it. Excluding it here makes
            // the address answer 550 — a visible failure — rather than filing into a space nobody reads.
            var active = await _dbContext.Users.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(u => u.Id == ownerId && u.TenantId == tenant && u.IsActive, cancellationToken);
            if (!active)
            {
                _logger.LogWarning(
                    "LMTP: {Address} is claimed by a deactivated user's mailbox; excluded from delivery", address);
                continue;
            }

            targets.Add(new DeliveryTarget(tenant, ownerId, null));
        }

        return targets;
    }

    /// <summary>Files the message and returns the LMTP reply line for THIS recipient.</summary>
    public async Task<string> DeliverAsync(string address, string sender, byte[] payload, CancellationToken cancellationToken)
    {
        var targets = await ResolveAsync(address, cancellationToken);
        if (targets.Count == 0)
        {
            // It resolved at RCPT and does not now — the account was removed mid-transaction. Permanent, so
            // the sender is told rather than the MTA retrying against a user who no longer exists.
            _logger.LogWarning("LMTP: {Address} resolved at RCPT but not at delivery; refusing permanently", address);
            return "550 no such recipient here";
        }

        // One RCPT, one copy into each resolved mailbox, ONE reply (#703 PR 3). Fan-out only exists where an
        // admin explicitly confirmed a duplicate claim, so several targets is a decision being honoured, not
        // an accident being amplified. The per-recipient reply discipline concerns multiple RCPTs — several
        // targets behind one recipient still answer as one.
        //
        // A partial failure defers the WHOLE recipient (451): the MTA redelivers, and the copies that already
        // landed will land again — a duplicate in a mailbox is recoverable by a reader, a message that
        // silently reached only half its claimants is not. The same trade every maildrop makes.
        foreach (var target in targets)
        {
            var (tenantId, userId, mailboxId, serviceAccountId) = target;
            try
            {
                // The DbContext reads the tenant from this accessor, and there is no request to have set it — the
                // MTA is the caller. Setting it here is what puts every query below inside the right tenant.
                ((CurrentTenantAccessor)_tenantAccessor).TenantId = tenantId;

                // Lazily rather than eagerly, and shared with the credential trigger: the mailbox exists exactly
                // when it has something to hold, and whichever of the two demands arrives first creates it (#562).
                // A DEPARTMENT mailbox's Inbox is the same idea one level in (#703 PR 4): the mailbox exists —
                // a person created it — and its Inbox appears with the first message.
                var inboxId = mailboxId is { } mailbox
                    ? await _mailbox.EnsureInboxForMailboxAsync(mailbox, cancellationToken)
                    : await _mailbox.EnsureInboxAsync(tenantId, userId!.Value, cancellationToken);

                var now = DateTimeOffset.UtcNow;
                var versionId = Guid.NewGuid();
                var storageFolderId = Guid.NewGuid();
                // The EPHEMERAL prefix, not an archive key (#633): a delivered message has not been filed, and its
                // bytes should not sit where the archive's retention and disposition rules apply until it is. It
                // moves onto an archive key when the user files it out — see DocumentMover. A department
                // mailbox's key files under the MAILBOX, because there is no user to file under and a path
                // that lies is a path someone debugs.
                var objectKey = mailboxId is { } forMailbox
                    ? ObjectKeyBuilder.DepartmentMailKey(tenantId, forMailbox, storageFolderId, versionId, ".eml")
                    : ObjectKeyBuilder.EphemeralMailKey(tenantId, userId!.Value, storageFolderId, versionId, ".eml");

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
                    CreatedByServiceAccountId = serviceAccountId,
                    CreatedAt = now,
                    StorageFolderId = storageFolderId,
                    // The retention clock starts here (#640). Delivered, not filed — until the user files it out,
                    // this is staging, and Junk/Trash sweep on this date.
                    StagedAt = now,
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
                    CreatedByServiceAccountId = serviceAccountId,
                    CreatedAt = now,
                    DocumentDate = DateOnly.FromDateTime(now.UtcDateTime),
                };
                _dbContext.DocumentVersions.Add(version);
                await _dbContext.SaveChangesAsync(cancellationToken);
                await _finalizer.FinalizeAsync(version, cancellationToken);

                _logger.LogInformation(
                    "Delivered a message from {Sender} to {Address} as document {DocumentId} ({Bytes} bytes)",
                    sender, address, document.Id, payload.Length);
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

        // Only now. Every copy above is durable.
        return "250 delivered";
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

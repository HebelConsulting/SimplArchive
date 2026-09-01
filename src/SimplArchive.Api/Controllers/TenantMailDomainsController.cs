using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.MailRouting;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The mail domains this tenant receives mail for, and how ownership of one is proven (#667, ADR 0692).
/// </summary>
/// <remarks>
/// <para>
/// Mail ingress resolves a recipient in two steps — domain to tenant, then local part to user (ADR 0628) — so
/// without a registered domain the feature refuses every message. It worked end to end from the day it shipped
/// and had <b>no supported way to be switched on</b>: the only code that ever wrote this table was the tests,
/// and the kiosk ran on a hand-written <c>INSERT</c>. This is the missing surface.
/// </para>
/// <para>
/// Gated on <c>CanManageMailRouting</c> — the right #703 introduced for a Mailbox's address list, which is the
/// same question one level down: who may decide where this tenant's mail comes from.
/// </para>
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenant/mail-domains")]
[Authorize]
public class TenantMailDomainsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IDnsTxtLookup _dns;
    private readonly IAuditRecorder _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<TenantMailDomainsController> _logger;

    public TenantMailDomainsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        IDnsTxtLookup dns,
        IAuditRecorder audit,
        TimeProvider time,
        ILogger<TenantMailDomainsController> logger)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _dns = dns;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    // Plain mutable classes — XmlSerializer (ADRs 0189/0190).
    public class MailDomainResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Domain { get; set; } = string.Empty;

        public bool Verified { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? VerifiedAt { get; set; }

        [System.Xml.Serialization.XmlElement(IsNullable = true)]
        public DateTimeOffset? LastCheckedAt { get; set; }

        /// <summary>The DNS name to publish the challenge at. Null once verified — there is nothing left to do.</summary>
        public string? ChallengeName { get; set; }

        /// <summary>The value to publish there. Null once verified, and for a domain declared by configuration.</summary>
        public string? ChallengeValue { get; set; }
    }

    public class MailDomainsResource : HypermediaResource
    {
        public List<MailDomainResource> Domains { get; set; } = [];

        /// <summary>Whether the caller may add, verify or remove — gates the admin UI (ADR 0543).</summary>
        public bool CanManage { get; set; }
    }

    public class AddMailDomainRequest
    {
        public string Domain { get; set; } = string.Empty;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var canManage = await CanManageAsync(cancellationToken);
        var domains = await _dbContext.TenantMailDomains
            .OrderBy(d => d.Domain)
            .ToListAsync(cancellationToken);

        var resource = new MailDomainsResource
        {
            Domains = [.. domains.Select(d => ToResource(d, canManage))],
            CanManage = canManage,
            Links = [new Link("self", "/api/tenant/mail-domains", "GET")],
        };

        // The create is a rel, so a client that may not add simply has no button rather than one that answers
        // 403 (ADR 0543) — the same reading CanManage gives the rest of the section.
        if (canManage)
        {
            resource.Links.Add(new Link("add", "/api/tenant/mail-domains", "POST"));
        }

        return Ok(resource);
    }

    // Standing convention: every GET action gets its own HEAD action.
    [HttpHead]
    public IActionResult Head() => NoContent();

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddMailDomainRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var domain = (request.Domain ?? string.Empty).Trim().TrimEnd('.');
        if (!MailDomainName.IsWellFormed(domain))
        {
            throw new InvalidMailDomainException(domain);
        }

        // The unique index is GLOBAL, deliberately (ADR 0628): a domain identifies exactly one tenant, so a
        // second claim is refused rather than allowed to make delivery ambiguous. Checked here as well as in
        // the index so the refusal is a sentence rather than a constraint violation — and NOT saying which
        // tenant holds it, which would leak one tenant's configuration to another.
        var normalized = domain.ToUpperInvariant();
        if (await _dbContext.TenantMailDomains.IgnoreQueryFilters(["TenantFilter"])
                .AnyAsync(d => d.NormalizedDomain == normalized, cancellationToken))
        {
            throw new MailDomainAlreadyClaimedException(domain);
        }

        var entry = new TenantMailDomain
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Domain = domain,
            CreatedAt = _time.GetUtcNow(),
            VerificationToken = MailDomainChallenge.NewToken(),
        };

        _dbContext.TenantMailDomains.Add(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Information: a completed unit of work with an outcome. It also states the half nobody asked for and
        // everybody needs — that no mail arrives yet — because a claim that looks done is precisely how a
        // domain sits unverified for a week.
        _logger.LogInformation(
            "Mail domain {Domain} claimed for tenant {TenantId}; unverified, so no mail is accepted for it yet.",
            domain, tenantId);

        await _audit.RecordAsync(
            AuditActions.MailDomainClaimed, "Tenant", tenantId, domain,
            $"Claimed the mail domain '{domain}'; awaiting DNS verification.", cancellationToken: cancellationToken);

        return Created($"/api/tenant/mail-domains/{entry.Id}", ToResource(entry, canManage: true));
    }

    /// <summary>Looks for the challenge in DNS now, and records the outcome.</summary>
    /// <remarks>
    /// A POST rather than a PUT: it is not the caller setting a value, it is the server going and finding out.
    /// Repeatable by design — an administrator publishes the record and presses it again.
    /// </remarks>
    [HttpPost("{id:guid}/verify")]
    public async Task<IActionResult> Verify(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken))
        {
            return Forbid();
        }

        if (await _dbContext.TenantMailDomains.FirstOrDefaultAsync(d => d.Id == id, cancellationToken) is not { } entry)
        {
            return NotFound();
        }

        if (entry.VerifiedAt is not null)
        {
            return Ok(ToResource(entry, canManage: true)); // already proven; nothing to look up
        }

        var records = await _dns.GetTxtRecordsAsync(entry.ChallengeName, cancellationToken);
        entry.LastCheckedAt = _time.GetUtcNow();

        // Trace carries the whole exchange (ADR 0626): what we looked for and what was actually published. This
        // is the seam where an administrator says "but I DID publish it" — and the answer is almost always a
        // stray quote, a trailing space, or the record on the apex instead of the challenge name. Neither side
        // is a secret: the token is published in public DNS by design.
        _logger.LogTrace(
            "Mail-domain challenge at {Name}: expected {Expected}, found {Found}",
            entry.ChallengeName, entry.VerificationToken, records);

        // Compared against the STORED token, and ordinally: a DNS TXT value is bytes, and a case-insensitive
        // match on a random token would only widen what counts as proof.
        var found = entry.VerificationToken is { } token
                    && records.Any(r => string.Equals(r.Trim(), token, StringComparison.Ordinal));

        if (!found)
        {
            await _dbContext.SaveChangesAsync(cancellationToken); // the attempt is recorded even when it fails

            // Debug, not Warning: the caller gets a machine-readable refusal it renders as a sentence naming
            // the record (ADR 0626's boundary), and this is the EXPECTED answer between publishing a record
            // and DNS propagating it. A Warning here would fire on every mistyped domain anyone tries.
            _logger.LogDebug(
                "Mail-domain verification for {Domain} found no matching record at {Name} ({Count} present).",
                entry.Domain, entry.ChallengeName, records.Count);

            throw new MailDomainNotVerifiedException(entry.Domain, entry.ChallengeName);
        }

        entry.VerifiedAt = _time.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken);

        // The moment the feature starts working for this tenant — worth a line at Information for the same
        // reason a sign-in is: it is the security-relevant success an operator correlates other events against.
        _logger.LogInformation(
            "Mail domain {Domain} verified for tenant {TenantId}; mail is now accepted for it.",
            entry.Domain, entry.TenantId);

        await _audit.RecordAsync(
            AuditActions.MailDomainVerified, "Tenant", entry.TenantId, entry.Domain,
            $"Verified ownership of '{entry.Domain}' by DNS challenge.", cancellationToken: cancellationToken);

        return Ok(ToResource(entry, canManage: true));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken))
        {
            return Forbid();
        }

        if (await _dbContext.TenantMailDomains.FirstOrDefaultAsync(d => d.Id == id, cancellationToken) is not { } entry)
        {
            return NotFound();
        }

        _dbContext.TenantMailDomains.Remove(entry);
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Warning rather than Information, alone among the three: this STOPS MAIL ARRIVING for everyone at the
        // domain, immediately, and the effect is only ever noticed by its absence. An administrator reading the
        // log after "we stopped receiving mail on Tuesday" should find this line without having to ask for it.
        _logger.LogWarning(
            "Mail domain {Domain} removed from tenant {TenantId}; mail addressed to it is no longer accepted.",
            entry.Domain, entry.TenantId);

        // Recorded because it STOPS MAIL ARRIVING, silently and immediately, for everyone at that domain —
        // the kind of change whose effect is only noticed by its absence.
        await _audit.RecordAsync(
            AuditActions.MailDomainRemoved, "Tenant", entry.TenantId, entry.Domain,
            $"Removed the mail domain '{entry.Domain}'; mail for it is no longer accepted.",
            cancellationToken: cancellationToken);

        return NoContent();
    }

    private MailDomainResource ToResource(TenantMailDomain entry, bool canManage)
    {
        var resource = new MailDomainResource
        {
            Id = entry.Id,
            Domain = entry.Domain,
            Verified = entry.VerifiedAt is not null,
            VerifiedAt = entry.VerifiedAt,
            LastCheckedAt = entry.LastCheckedAt,

            // The challenge is shown only while it is still the thing to do. A verified domain that kept
            // publishing its token would invite someone to "tidy up" the record it no longer needs, and a
            // domain declared by configuration never had one.
            ChallengeName = entry.VerifiedAt is null ? entry.ChallengeName : null,
            ChallengeValue = entry.VerifiedAt is null ? entry.VerificationToken : null,
        };

        resource.Links.Add(new Link("self", $"/api/tenant/mail-domains/{entry.Id}", "GET"));
        if (canManage)
        {
            resource.Links.Add(new Link("remove", $"/api/tenant/mail-domains/{entry.Id}", "DELETE"));

            // Offered only while there is something to verify — a rel that is absent because the work is done
            // reads as "not available here", which is exactly right (ADR 0543).
            if (entry.VerifiedAt is null)
            {
                resource.Links.Add(new Link("verify", $"/api/tenant/mail-domains/{entry.Id}/verify", "POST"));
            }
        }

        return resource;
    }

    private async Task<bool> CanManageAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageMailRouting;
}

using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Api.Errors.Exceptions.Authorization;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.ServiceAccounts;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "ServiceAccount management endpoints" — the only Api path to create/rotate-secret/revoke
/// a ServiceAccount; previously this only ever happened via direct DB/OpenIddict seeding. Every action
/// requires the caller's own CanManageServiceAccounts — either a ServiceAccount or a logged-in User (see
/// ADR "User support for ServiceAccount/User/Group/Mask management endpoints"); POST additionally caps the
/// 3 system-level rights it can hand to the new account at the caller's own current rights (same
/// philosophy as EffectiveRights.Covers, ADR "ACL management right").
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/service-accounts")]
[Authorize]
public class ServiceAccountsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IOpenIddictApplicationManager _applicationManager;
    private readonly IAuditRecorder _audit;

    public ServiceAccountsController(
        SimplArchiveDbContext dbContext,
        ICurrentTenantAccessor currentTenantAccessor,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IUserSystemRightsResolver userSystemRights,
        IOpenIddictApplicationManager applicationManager,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _currentTenantAccessor = currentTenantAccessor;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _userSystemRights = userSystemRights;
        _applicationManager = applicationManager;
        _audit = audit;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class ServiceAccountResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        /// <summary>
        /// Whether the caller may change this account — edit it, revoke it, rotate its secret. The SERVER's
        /// answer, not a re-derivation of <see cref="IsActive"/> by the client (issue #416): the rule that a
        /// revoked account cannot be edited belongs here, and can change here, without every client learning it.
        /// </summary>
        /// <remarks>
        /// This is ADR 0719's second half. `edit[PUT]` and `revoke[DELETE]` used to say it by being *absent*,
        /// which conflated two facts in one signal — where the resource is, and whether you may act on it. One
        /// rel now carries the address and this flag carries the permission.
        /// </remarks>
        public bool CanManage { get; set; }

        public bool CanManageRepositories { get; set; }

        public bool CanManageMasks { get; set; }

        public bool CanManageServiceAccounts { get; set; }

        public bool CanImport { get; set; }

        public bool CanExport { get; set; }
    }

    public class CreateServiceAccountResource : ServiceAccountResource
    {
        // Present only in the create response — never retrievable again afterward, OpenIddict only ever
        // stores it hashed.
        public string ClientSecret { get; set; } = string.Empty;
    }

    public class ServiceAccountsListResource : HypermediaResource
    {
        public List<ServiceAccountResource> ServiceAccounts { get; set; } = [];

        /// <summary>Which rights THIS caller may confer on a service account (#864).</summary>
        /// <remarks>
        /// <para>
        /// The server caps a grant at what the caller holds — "can't hand out more than you hold", the same
        /// philosophy as <c>EffectiveRights.Covers</c> — and answers a violation with
        /// <c>403 INSUFFICIENT_RIGHTS_TO_GRANT</c>. Until now it advertised **nothing**, so the edit dialog
        /// offered all five rights uncapped and its own code-behind admitted it: *"the API caps… this dialog
        /// just collects"*. That is the promise ADR 0543 exists to prevent, and unlike the other findings in
        /// this epic it could not be fixed client-side, because the answer genuinely was not on the wire.
        /// </para>
        /// <para>
        /// It rides on the COLLECTION, not on each row: the cap is a property of the caller, identical for
        /// creating a new account and for editing any existing one. Per row it would be the same answer
        /// repeated, and a reader would reasonably wonder why two rows might differ.
        /// </para>
        /// <para>
        /// A right absent here means the caller may not grant it — nor revoke it, since the check is on the
        /// VALUE changing, not on its direction.
        /// </para>
        /// </remarks>
        public GrantableServiceAccountRights GrantableRights { get; set; } = new();
    }

    /// <summary>The five service-account rights, as booleans the caller may confer (#864).</summary>
    /// <remarks>
    /// Deliberately the same five names the request body uses, so a client maps one to the other without a
    /// translation table — a table being the place a tenth right would later be forgotten.
    /// </remarks>
    public class GrantableServiceAccountRights
    {
        public bool CanManageRepositories { get; set; }

        public bool CanManageMasks { get; set; }

        public bool CanManageServiceAccounts { get; set; }

        public bool CanImport { get; set; }

        public bool CanExport { get; set; }
    }

    public class CreateServiceAccountRequest
    {
        public string Name { get; set; } = string.Empty;

        public bool CanManageRepositories { get; set; }

        public bool CanManageMasks { get; set; }

        public bool CanManageServiceAccounts { get; set; }

        public bool CanImport { get; set; }

        public bool CanExport { get; set; }
    }

    // Edit an existing account's name + rights (ADR 0534). Same shape as create minus the secret — a plain
    // full-replace PUT (SA carries no ConcurrencyToken, so no If-Match, matching this controller's other mutations).
    public class UpdateServiceAccountRequest
    {
        public string Name { get; set; } = string.Empty;

        public bool CanManageRepositories { get; set; }

        public bool CanManageMasks { get; set; }

        public bool CanManageServiceAccounts { get; set; }

        public bool CanImport { get; set; }

        public bool CanExport { get; set; }
    }

    public class RotateSecretResource : HypermediaResource
    {
        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateServiceAccountRequest request, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        // Same "can't hand out more than you hold" philosophy as EffectiveRights.Covers (ADR "ACL
        // management right") — three plain inline checks rather than a shared abstraction, not worth
        // extracting a generic mechanism for three fields.
        if ((request.CanManageRepositories && !caller.CanManageRepositories)
            || (request.CanManageMasks && !caller.CanManageMasks)
            || (request.CanManageServiceAccounts && !caller.CanManageServiceAccounts)
            || (request.CanImport && !caller.CanImport)
            || (request.CanExport && !caller.CanExport))
        {
            throw InsufficientRightsToGrantException.OnServiceAccount();
        }

        var tenantId = _currentTenantAccessor.TenantId!.Value;
        var clientId = Guid.NewGuid().ToString();
        var clientSecret = GenerateClientSecret();

        var serviceAccount = new ServiceAccount
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = request.Name,
            OpenIddictApplicationClientId = clientId,
            IsActive = true,
            CanManageRepositories = request.CanManageRepositories,
            CanManageMasks = request.CanManageMasks,
            CanManageServiceAccounts = request.CanManageServiceAccounts,
            CanImport = request.CanImport,
            CanExport = request.CanExport,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.ServiceAccounts.Add(serviceAccount);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // (TenantId, Name) is a real DB unique index, not a SaveChanges-enforced app-level check like
            // Document/Group sibling names — this is the first controller to write against a
            // ServiceAccount-style plain unique index with no existing pre-check.
            throw new ServiceAccountNameConflictException();
        }

        await _applicationManager.CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            Permissions =
            {
                OpenIddictConstants.Permissions.Endpoints.Token,
                OpenIddictConstants.Permissions.GrantTypes.ClientCredentials,
            },
        }, cancellationToken);

        var resource = new CreateServiceAccountResource
        {
            Id = serviceAccount.Id,
            Name = serviceAccount.Name,
            ClientId = clientId,
            ClientSecret = clientSecret,
            IsActive = serviceAccount.IsActive,
            // The server's answer to "may this be changed?" (ADR 0719). Today it is exactly IsActive — but it
            // is computed HERE, so tightening the rule never means teaching two clients a new one.
            CanManage = serviceAccount.IsActive,
            CanManageRepositories = serviceAccount.CanManageRepositories,
            CanManageMasks = serviceAccount.CanManageMasks,
            CanManageServiceAccounts = serviceAccount.CanManageServiceAccounts,
            CanImport = serviceAccount.CanImport,
            CanExport = serviceAccount.CanExport,
            Links = [new Link("self", $"/api/service-accounts/{serviceAccount.Id}", "GET")],
        };

        await _audit.RecordAsync(AuditActions.ServiceAccountCreated, "ServiceAccount", serviceAccount.Id, serviceAccount.Name, cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(Get), new { serviceAccountId = serviceAccount.Id }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.ServiceAccounts.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(s => s.CreatedAt > cursorCreatedAt || (s.CreatedAt == cursorCreatedAt && s.Id > cursorId));
        }

        var fetched = await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new ServiceAccountsListResource
        {
            ServiceAccounts = page.Select(BuildResource).ToList(),
            // Straight from the rights this action ALREADY resolved to authorize itself (#864) — so the
            // advertised cap and the enforced one are the same value, not two reads that agree (ADR 0722).
            GrantableRights = new GrantableServiceAccountRights
            {
                CanManageRepositories = caller.CanManageRepositories,
                CanManageMasks = caller.CanManageMasks,
                CanManageServiceAccounts = caller.CanManageServiceAccounts,
                CanImport = caller.CanImport,
                CanExport = caller.CanExport,
            },
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public async Task<IActionResult> HeadList(CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{serviceAccountId:guid}")]
    public async Task<IActionResult> Get(Guid serviceAccountId, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        var serviceAccount = await _dbContext.ServiceAccounts.SingleOrDefaultAsync(s => s.Id == serviceAccountId, cancellationToken);

        if (serviceAccount is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(serviceAccount));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{serviceAccountId:guid}")]
    public async Task<IActionResult> Head(Guid serviceAccountId, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        var exists = await _dbContext.ServiceAccounts.AnyAsync(s => s.Id == serviceAccountId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    // Edit an existing account's name + rights (ADR 0534) — a full-replace PUT. Escalation-capped exactly like
    // Create: a caller can only SET a right to true that it holds itself, so it can never authorise an account
    // beyond its own reach. The secret is untouched (rotate-secret owns that); revoked accounts can still be
    // edited but stay revoked (IsActive is not a right here).
    [HttpPut("{serviceAccountId:guid}")]
    public async Task<IActionResult> Update(Guid serviceAccountId, [FromBody] UpdateServiceAccountRequest request, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        if ((request.CanManageRepositories && !caller.CanManageRepositories)
            || (request.CanManageMasks && !caller.CanManageMasks)
            || (request.CanManageServiceAccounts && !caller.CanManageServiceAccounts)
            || (request.CanImport && !caller.CanImport)
            || (request.CanExport && !caller.CanExport))
        {
            throw InsufficientRightsToGrantException.OnServiceAccount();
        }

        var serviceAccount = await _dbContext.ServiceAccounts.SingleOrDefaultAsync(s => s.Id == serviceAccountId, cancellationToken);

        if (serviceAccount is null)
        {
            return NotFound();
        }

        serviceAccount.Name = request.Name;
        serviceAccount.CanManageRepositories = request.CanManageRepositories;
        serviceAccount.CanManageMasks = request.CanManageMasks;
        serviceAccount.CanManageServiceAccounts = request.CanManageServiceAccounts;
        serviceAccount.CanImport = request.CanImport;
        serviceAccount.CanExport = request.CanExport;

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // (TenantId, Name) is a real DB unique index — a rename onto an existing name collides here.
            throw new ServiceAccountNameConflictException();
        }

        await _audit.RecordAsync(AuditActions.ServiceAccountUpdated, "ServiceAccount", serviceAccount.Id, serviceAccount.Name, cancellationToken: cancellationToken);

        return Ok(BuildResource(serviceAccount));
    }

    // An action endpoint, not idempotent — each call mints a genuinely new secret and immediately
    // invalidates the old one. Modeled the same way POST /documents/{id}/restore already is, rather than
    // forced into PUT's idempotent-replace contract. See ADR "ServiceAccount management endpoints".
    [HttpPost("{serviceAccountId:guid}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid serviceAccountId, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        var serviceAccount = await _dbContext.ServiceAccounts.SingleOrDefaultAsync(s => s.Id == serviceAccountId, cancellationToken);

        if (serviceAccount is null)
        {
            return NotFound();
        }

        var application = await _applicationManager.FindByClientIdAsync(serviceAccount.OpenIddictApplicationClientId, cancellationToken);

        if (application is null)
        {
            throw OpenIddictApplicationNotFoundException.ForServiceAccount();
        }

        var newSecret = GenerateClientSecret();
        await _applicationManager.UpdateAsync(application, newSecret, cancellationToken);

        await _audit.RecordAsync(AuditActions.ServiceAccountSecretRotated, "ServiceAccount", serviceAccount.Id, serviceAccount.Name, cancellationToken: cancellationToken);

        return Ok(new RotateSecretResource
        {
            ClientId = serviceAccount.OpenIddictApplicationClientId,
            ClientSecret = newSecret,
            Links = [new Link("self", $"/api/service-accounts/{serviceAccountId}", "GET")],
        });
    }

    // Revokes: sets IsActive = false in place, the row is not deleted. TokenController already rejects
    // token issuance for an inactive ServiceAccount, so no separate OpenIddict-side disable is needed.
    // One-way, no reactivation endpoint — a revoked ServiceAccount stays revoked; getting a working
    // credential again means creating a new one. See ADR "ServiceAccount management endpoints".
    [HttpDelete("{serviceAccountId:guid}")]
    public async Task<IActionResult> Revoke(Guid serviceAccountId, CancellationToken cancellationToken)
    {
        var caller = await GetCallerRightsAsync(cancellationToken);

        if (caller is null || !caller.CanManageServiceAccounts)
        {
            return Forbid();
        }

        var serviceAccount = await _dbContext.ServiceAccounts.SingleOrDefaultAsync(s => s.Id == serviceAccountId, cancellationToken);

        if (serviceAccount is null)
        {
            return NotFound();
        }

        serviceAccount.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _audit.RecordAsync(AuditActions.ServiceAccountRevoked, "ServiceAccount", serviceAccount.Id, serviceAccount.Name, cancellationToken: cancellationToken);

        return NoContent();
    }

    private static string GenerateClientSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static ServiceAccountResource BuildResource(ServiceAccount serviceAccount)
    {
        return new ServiceAccountResource
        {
            Id = serviceAccount.Id,
            Name = serviceAccount.Name,
            ClientId = serviceAccount.OpenIddictApplicationClientId,
            IsActive = serviceAccount.IsActive,
            // The server's answer to "may this be changed?" (ADR 0719). Today it is exactly IsActive — but it
            // is computed HERE, so tightening the rule never means teaching two clients a new one.
            CanManage = serviceAccount.IsActive,
            CanManageRepositories = serviceAccount.CanManageRepositories,
            CanManageMasks = serviceAccount.CanManageMasks,
            CanManageServiceAccounts = serviceAccount.CanManageServiceAccounts,
            CanImport = serviceAccount.CanImport,
            CanExport = serviceAccount.CanExport,

            // ONE rel for this address; the method says which action (ADR 0719). `edit[PUT]` and
            // `revoke[DELETE]` sat beside `self[GET]` on the same URL and said nothing the method did not
            // already carry — so what they really encoded was CanManage, by being absent on a revoked account.
            // That is now stated as the capability it is, which keeps the server the one deciding (issue #416)
            // while letting the address be advertised once.
            //
            // `rotate-secret` stays a rel: it is a DIFFERENT address, and it stays conditional because an
            // affordance whose outcome is already decided should be absent rather than offered (ADR 0543).
            Links = serviceAccount.IsActive
                ?
                [
                    new Link("self", $"/api/service-accounts/{serviceAccount.Id}", "GET"),
                    new Link("rotate-secret", $"/api/service-accounts/{serviceAccount.Id}/rotate-secret", "POST"),
                ]
                : [new Link("self", $"/api/service-accounts/{serviceAccount.Id}", "GET")],
        };
    }

    // The caller's rights relevant to this controller — the same field names whether the caller is a
    // ServiceAccount or a User. CanImport/CanExport join the three management rights so they can be
    // escalation-capped when granted at creation (ADR 0523). See ADR "User support for ServiceAccount/User/
    // Group/Mask management endpoints".
    private record CallerRights(
        bool CanManageRepositories,
        bool CanManageMasks,
        bool CanManageServiceAccounts,
        bool CanImport,
        bool CanExport);

    private async Task<CallerRights?> GetCallerRightsAsync(CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _dbContext.ServiceAccounts
                .Where(s => s.Id == serviceAccountId)
                .Select(s => new CallerRights(s.CanManageRepositories, s.CanManageMasks, s.CanManageServiceAccounts, s.CanImport, s.CanExport))
                .SingleOrDefaultAsync(cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            // Effective rights (own ∪ groups) so a management right held via a group takes effect (and is
            // grantable by the escalation cap) — ADR "Enforce group system rights for members".
            var r = await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken);
            return new CallerRights(r.CanManageRepositories, r.CanManageMasks, r.CanManageServiceAccounts, r.CanImport, r.CanExport);
        }

        return null;
    }
}

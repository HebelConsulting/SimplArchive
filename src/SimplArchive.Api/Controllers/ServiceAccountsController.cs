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

        public string Name { get; set; } = "";

        public string ClientId { get; set; } = "";

        public bool IsActive { get; set; }

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
        public string ClientSecret { get; set; } = "";
    }

    public class ServiceAccountsListResource : HypermediaResource
    {
        public List<ServiceAccountResource> ServiceAccounts { get; set; } = [];
    }

    public class CreateServiceAccountRequest
    {
        public string Name { get; set; } = "";

        public bool CanManageRepositories { get; set; }

        public bool CanManageMasks { get; set; }

        public bool CanManageServiceAccounts { get; set; }

        public bool CanImport { get; set; }

        public bool CanExport { get; set; }
    }

    public class RotateSecretResource : HypermediaResource
    {
        public string ClientId { get; set; } = "";

        public string ClientSecret { get; set; } = "";
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
            CanManageRepositories = serviceAccount.CanManageRepositories,
            CanManageMasks = serviceAccount.CanManageMasks,
            CanManageServiceAccounts = serviceAccount.CanManageServiceAccounts,
            CanImport = serviceAccount.CanImport,
            CanExport = serviceAccount.CanExport,
            Links = [new Link("self", $"/api/service-accounts/{serviceAccount.Id}", "GET")],
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

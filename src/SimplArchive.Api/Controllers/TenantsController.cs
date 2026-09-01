using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Api.Provisioning;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Tenants;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "Tenant onboarding and platform-admin mechanism", redesigned by ADR "Document-scope
/// authorization retrofit for User, and tenant-administrator-driven onboarding" — the only Api path to
/// create a Tenant; previously this only ever happened via direct DB seeding. Every action requires the
/// caller to be a genuine, authenticated PlatformAdministrator — a valid ServiceAccount token isn't
/// enough, even though it's a valid token, since it isn't a platform-admin one.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tenants")]
[Authorize]
public class TenantsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentPlatformAdministratorAccessor _currentPlatformAdministratorAccessor;
    private readonly ITenantProvisioningService _tenantProvisioningService;

    private readonly IAuditRecorder _audit;

    public TenantsController(
        SimplArchiveDbContext dbContext,
        ICurrentPlatformAdministratorAccessor currentPlatformAdministratorAccessor,
        ITenantProvisioningService tenantProvisioningService,
        IAuditRecorder audit)
    {
        _dbContext = dbContext;
        _currentPlatformAdministratorAccessor = currentPlatformAdministratorAccessor;
        _tenantProvisioningService = tenantProvisioningService;
        _audit = audit;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class TenantResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class TenantsListResource : HypermediaResource
    {
        public List<TenantResource> Tenants { get; set; } = [];
    }

    public class CreateTenantRequest
    {
        public string Name { get; set; } = string.Empty;

        public string AdministratorEmail { get; set; } = string.Empty;

        public string AdministratorDisplayName { get; set; } = string.Empty;

        // Defaults to the tenant's own Name if omitted.
        public string? RepositoryName { get; set; }
    }

    public class TenantAdministratorResource
    {
        public Guid Id { get; set; }

        public string Email { get; set; } = string.Empty;

        // Present only here — never retrievable again, only ever stored hashed (PasswordHasher<User>).
        public string Password { get; set; } = string.Empty;
    }

    public class CreatedRepositoryResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public class CreateTenantResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public TenantAdministratorResource TenantAdministrator { get; set; } = new();

        public CreatedRepositoryResource Repository { get; set; } = new();
    }

    // Creates the Tenant, seeds its 3 well-known masks, and provisions a TenantAdministrator User
    // (IsTenantAdmin = true, plus CanManageRepositories/CanManageMasks/CanManageServiceAccounts) who
    // creates the tenant's first repository itself — a real principal AclEntry/Document.CreatedByUserId
    // already fully support, unlike PlatformAdministrator, which isn't a valid AclEntry principal at all.
    // This is the one place in the whole Api where granting full rights with no escalation cap is sound: a
    // PlatformAdministrator delegates trust into a brand-new tenant it's creating, unlike
    // AclEntry/ServiceAccount/User creation, which cap what a caller can hand out from its own existing
    // rights within an already-existing tenant. See ADR "Document-scope authorization retrofit for User,
    // and tenant-administrator-driven onboarding" (supersedes the original bootstrap-ServiceAccount shape
    // from ADR "Tenant onboarding and platform-admin mechanism").
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTenantRequest request, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        // administratorPassword: null — the HTTP path always generates a random initial password (returned
        // once below). The demo seeder is the caller that passes an explicit one. The provisioning logic
        // itself lives in the shared service so it isn't duplicated between here and that seeder.
        var provisioned = await _tenantProvisioningService.ProvisionAsync(
            request.Name,
            request.AdministratorEmail,
            request.AdministratorDisplayName,
            request.RepositoryName,
            administratorPassword: null,
            cancellationToken);

        var resource = new CreateTenantResource
        {
            Id = provisioned.TenantId,
            Name = provisioned.TenantName,
            TenantAdministrator = new TenantAdministratorResource
            {
                Id = provisioned.AdministratorId,
                Email = provisioned.AdministratorEmail,
                Password = provisioned.AdministratorPassword,
            },
            Repository = new CreatedRepositoryResource
            {
                Id = provisioned.RepositoryId,
                Name = provisioned.RepositoryName,
            },
            Links = [new Link("self", $"/api/tenants/{provisioned.TenantId}", "GET")],
        };

        // The actor is a PlatformAdministrator with no current tenant; the event belongs to the
        // just-created tenant, so its id is passed explicitly rather than read from the accessor.
        await _audit.RecordAsync(
            AuditActions.TenantCreated,
            "Tenant",
            provisioned.TenantId,
            provisioned.TenantName,
            tenantId: provisioned.TenantId,
            cancellationToken: cancellationToken);

        return CreatedAtAction(nameof(Get), new { tenantId = provisioned.TenantId }, resource);
    }

    // Cursor-based pagination (?cursor=&limit=) — see ADR "Pagination for list endpoints". Sorted
    // CreatedAt ascending, Id ascending as tiebreaker.
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var pageSize = PageSize.Resolve(limit);

        var query = _dbContext.Tenants.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(t => t.CreatedAt > cursorCreatedAt || (t.CreatedAt == cursorCreatedAt && t.Id > cursorId));
        }

        var fetched = await query.OrderBy(t => t.CreatedAt).ThenBy(t => t.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new TenantsListResource
        {
            Tenants = page.Select(BuildResource).ToList(),
            Links = links,
        });
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead]
    public IActionResult HeadList()
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        return NoContent();
    }

    [HttpGet("{tenantId:guid}")]
    public async Task<IActionResult> Get(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var tenant = await _dbContext.Tenants.SingleOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(tenant));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{tenantId:guid}")]
    public async Task<IActionResult> Head(Guid tenantId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var exists = await _dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    private static TenantResource BuildResource(Tenant tenant)
    {
        return new TenantResource
        {
            Id = tenant.Id,
            Name = tenant.Name,
            Links = [new Link("self", $"/api/tenants/{tenant.Id}", "GET")],
        };
    }
}

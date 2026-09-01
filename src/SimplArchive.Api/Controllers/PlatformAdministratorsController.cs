using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Principals;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.PlatformAdministrators;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Implements ADR "Tenant onboarding and platform-admin mechanism" — a minimal, self-gated management
/// surface: any active PlatformAdministrator can create/list/read/rotate/revoke another. Included now
/// rather than deferred like ServiceAccountsController was after the ServiceAccount entity first landed.
/// No rights matrix — being a genuine, active PlatformAdministrator is itself sufficient for every action.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/platform-administrators")]
[Authorize]
public class PlatformAdministratorsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentPlatformAdministratorAccessor _currentPlatformAdministratorAccessor;
    private readonly IOpenIddictApplicationManager _applicationManager;

    public PlatformAdministratorsController(
        SimplArchiveDbContext dbContext,
        ICurrentPlatformAdministratorAccessor currentPlatformAdministratorAccessor,
        IOpenIddictApplicationManager applicationManager)
    {
        _dbContext = dbContext;
        _currentPlatformAdministratorAccessor = currentPlatformAdministratorAccessor;
        _applicationManager = applicationManager;
    }

    // Plain mutable classes, not records — System.Xml.Serialization.XmlSerializer (ADR "JSON/XML content
    // negotiation") needs a parameterless constructor and settable properties.
    public class PlatformAdministratorResource : HypermediaResource
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string ClientId { get; set; } = string.Empty;

        public bool IsActive { get; set; }
    }

    public class CreatePlatformAdministratorResource : PlatformAdministratorResource
    {
        // Present only in the create response — never retrievable again, OpenIddict only ever stores it
        // hashed.
        public string ClientSecret { get; set; } = string.Empty;
    }

    public class PlatformAdministratorsListResource : HypermediaResource
    {
        public List<PlatformAdministratorResource> PlatformAdministrators { get; set; } = [];
    }

    public class CreatePlatformAdministratorRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    public class RotateSecretResource : HypermediaResource
    {
        public string ClientId { get; set; } = string.Empty;

        public string ClientSecret { get; set; } = string.Empty;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePlatformAdministratorRequest request, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var clientId = Guid.NewGuid().ToString();
        var clientSecret = GenerateClientSecret();

        var platformAdministrator = new PlatformAdministrator
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            OpenIddictApplicationClientId = clientId,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _dbContext.PlatformAdministrators.Add(platformAdministrator);

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new PlatformAdministratorNameConflictException();
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

        var resource = new CreatePlatformAdministratorResource
        {
            Id = platformAdministrator.Id,
            Name = platformAdministrator.Name,
            ClientId = clientId,
            ClientSecret = clientSecret,
            IsActive = platformAdministrator.IsActive,
            Links = [new Link("self", $"/api/platform-administrators/{platformAdministrator.Id}", "GET")],
        };

        return CreatedAtAction(nameof(Get), new { platformAdministratorId = platformAdministrator.Id }, resource);
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

        var query = _dbContext.PlatformAdministrators.AsQueryable();

        if (Cursor.TryDecode(cursor, out var cursorCreatedAt, out var cursorId))
        {
            query = query.Where(p => p.CreatedAt > cursorCreatedAt || (p.CreatedAt == cursorCreatedAt && p.Id > cursorId));
        }

        var fetched = await query.OrderBy(p => p.CreatedAt).ThenBy(p => p.Id).Take(pageSize + 1).ToListAsync(cancellationToken);
        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET") };

        if (hasMore)
        {
            var nextCursor = Cursor.Encode(page[^1].CreatedAt, page[^1].Id);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = nextCursor, limit = pageSize })!, "GET"));
        }

        return Ok(new PlatformAdministratorsListResource
        {
            PlatformAdministrators = page.Select(BuildResource).ToList(),
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

    [HttpGet("{platformAdministratorId:guid}")]
    public async Task<IActionResult> Get(Guid platformAdministratorId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var platformAdministrator = await _dbContext.PlatformAdministrators.SingleOrDefaultAsync(p => p.Id == platformAdministratorId, cancellationToken);

        if (platformAdministrator is null)
        {
            return NotFound();
        }

        return Ok(BuildResource(platformAdministrator));
    }

    // Standing convention: every GET action gets a companion HEAD action — a separate action, not relying
    // on ASP.NET Core to strip GET's body automatically.
    [HttpHead("{platformAdministratorId:guid}")]
    public async Task<IActionResult> Head(Guid platformAdministratorId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var exists = await _dbContext.PlatformAdministrators.AnyAsync(p => p.Id == platformAdministratorId, cancellationToken);

        return exists ? NoContent() : NotFound();
    }

    // Non-idempotent action endpoint, same shape as ServiceAccountsController.RotateSecret — each call
    // mints a genuinely new secret and immediately invalidates the old one.
    [HttpPost("{platformAdministratorId:guid}/rotate-secret")]
    public async Task<IActionResult> RotateSecret(Guid platformAdministratorId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var platformAdministrator = await _dbContext.PlatformAdministrators.SingleOrDefaultAsync(p => p.Id == platformAdministratorId, cancellationToken);

        if (platformAdministrator is null)
        {
            return NotFound();
        }

        var application = await _applicationManager.FindByClientIdAsync(platformAdministrator.OpenIddictApplicationClientId, cancellationToken);

        if (application is null)
        {
            throw OpenIddictApplicationNotFoundException.ForPlatformAdministrator();
        }

        var newSecret = GenerateClientSecret();
        await _applicationManager.UpdateAsync(application, newSecret, cancellationToken);

        return Ok(new RotateSecretResource
        {
            ClientId = platformAdministrator.OpenIddictApplicationClientId,
            ClientSecret = newSecret,
            Links = [new Link("self", $"/api/platform-administrators/{platformAdministratorId}", "GET")],
        });
    }

    // Revokes: sets IsActive = false in place, one-way, same as ServiceAccountsController.Revoke. New
    // safety check not present there: rejected if this would leave zero active PlatformAdministrators —
    // unlike a ServiceAccount, losing every PlatformAdministrator re-triggers the deployment-level
    // bootstrap gap with no Api path back. See ADR "Tenant onboarding and platform-admin mechanism".
    [HttpDelete("{platformAdministratorId:guid}")]
    public async Task<IActionResult> Revoke(Guid platformAdministratorId, CancellationToken cancellationToken)
    {
        if (_currentPlatformAdministratorAccessor.PlatformAdministratorId is null)
        {
            return Forbid();
        }

        var platformAdministrator = await _dbContext.PlatformAdministrators.SingleOrDefaultAsync(p => p.Id == platformAdministratorId, cancellationToken);

        if (platformAdministrator is null)
        {
            return NotFound();
        }

        if (platformAdministrator.IsActive)
        {
            var otherActiveCount = await _dbContext.PlatformAdministrators.CountAsync(p => p.IsActive && p.Id != platformAdministratorId, cancellationToken);

            if (otherActiveCount == 0)
            {
                throw new LastPlatformAdministratorException();
            }
        }

        platformAdministrator.IsActive = false;
        await _dbContext.SaveChangesAsync(cancellationToken);

        return NoContent();
    }

    private static string GenerateClientSecret()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static PlatformAdministratorResource BuildResource(PlatformAdministrator platformAdministrator)
    {
        return new PlatformAdministratorResource
        {
            Id = platformAdministrator.Id,
            Name = platformAdministrator.Name,
            ClientId = platformAdministrator.OpenIddictApplicationClientId,
            IsActive = platformAdministrator.IsActive,
            Links = [new Link("self", $"/api/platform-administrators/{platformAdministrator.Id}", "GET")],
        };
    }
}

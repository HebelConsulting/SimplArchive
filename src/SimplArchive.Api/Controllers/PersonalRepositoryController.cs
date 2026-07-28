using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The logged-in user's personal repository (ADR "Per-user personal repository") — a root Document flagged with
/// PersonalOfUserId, named "Personal", ACL-owned by the user. `POST` is get-or-create (idempotent): the client
/// calls it once on load to ensure the space exists and get its id, then browses it like any repository. A
/// ServiceAccount has no personal repository. The repository is excluded from the shared GET /repositories list.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/me/personal-repository")]
[Authorize]
public class PersonalRepositoryController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly Documents.PersonalRepositoryProvisioner _provisioner;

    public PersonalRepositoryController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor, ICurrentTenantAccessor currentTenantAccessor, Documents.PersonalRepositoryProvisioner provisioner)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _provisioner = provisioner;
    }

    public class PersonalRepositoryResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        // For the client's tree node (same computed flags as the repository-list resources).
        public bool HasChildren { get; set; }
        public bool HasSubfolders { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Ensure(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid(); // a ServiceAccount / platform admin has no personal space
        }

        var document = await _provisioner.EnsureAsync(userId, tenantId, cancellationToken);
        return Ok(await ToResourceAsync(document.Id, document.Name, cancellationToken));
    }

    private async Task<PersonalRepositoryResource> ToResourceAsync(Guid id, string name, CancellationToken cancellationToken)
    {
        // Same computed flags the repository-list/child-listing resources carry (ADR "Blazor repository/document
        // browsing"): hasChildren drives the contents drill-in, hasSubfolders the folders-only tree's expand caret.
        var hasChildren = await _dbContext.Documents.AnyAsync(c => c.ParentId == id, cancellationToken);
        var hasSubfolders = await _dbContext.Documents.AnyAsync(
            c => c.ParentId == id && !_dbContext.DocumentVersions.Any(v => v.DocumentId == c.Id), cancellationToken);

        return new PersonalRepositoryResource
        {
            Id = id,
            Name = name,
            HasChildren = hasChildren,
            HasSubfolders = hasSubfolders,
            Links =
            [
                new Link("self", "/api/me/personal-repository", "POST"),
                new Link("children", $"/api/documents/{id}/children", "GET"),
                new Link("document", $"/api/documents/{id}", "GET"),
            ],
        };
    }
}

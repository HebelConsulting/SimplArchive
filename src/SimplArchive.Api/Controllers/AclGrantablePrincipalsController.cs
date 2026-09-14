using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The users/groups/service-accounts a manager may grant to on one document — the Manage-access dialog's picker
/// (ADR "Manage-access UI for document/folder ACLs").
/// </summary>
/// <remarks>
/// A sibling controller on <see cref="AclEntriesController"/>'s own route, the shape ADR 0571 established when
/// <c>DocumentsController</c> was split five ways. Split out because converting the ACL writes to their verb
/// contract (ADR 0795) took that file to exactly 1000 lines, and the standing rule is to split by
/// responsibility rather than take an exception nobody granted.
///
/// This is the right seam rather than a convenient one: everything here READS candidate principals, while what
/// remains there WRITES grants. That difference also shows up in the concurrency ratchet, which flagged
/// <c>AclEntriesController</c> for naming the <c>Users</c> and <c>ServiceAccounts</c> sets it only ever read.
///
/// Gated on <c>CanManagePermissions</c> on THIS document rather than on <c>CanManageUsers</c>, so a permissions
/// manager who is not a user-admin can still populate the picker (the same reasoning as assignable-reviewers).
/// Bounded, not paginated.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/acl-entries")]
[Authorize]
public class AclGrantablePrincipalsController(
    SimplArchiveDbContext dbContext, Documents.DocumentAccessService access) : ControllerBase
{
    public class GrantablePrincipalsResource : HypermediaResource
    {
        public List<GrantablePrincipal> Principals { get; set; } = [];
    }

    public class GrantablePrincipal : HypermediaResource
    {
        public string Type { get; set; } = string.Empty;   // users | groups | service-accounts
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    [HttpGet("grantable-principals")]
    public async Task<IActionResult> GrantablePrincipals(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!await access.CanManagePermissionsAsync(documentId, cancellationToken))
        {
            return Forbid();
        }

        var groups = await dbContext.Groups.OrderBy(g => g.Name)
            .Select(g => new GrantablePrincipal { Type = "groups", Id = g.Id, Name = g.Name })
            .ToListAsync(cancellationToken);
        var users = await dbContext.Users.Where(u => u.IsActive).OrderBy(u => u.DisplayName)
            .Select(u => new GrantablePrincipal { Type = "users", Id = u.Id, Name = u.DisplayName })
            .ToListAsync(cancellationToken);
        var serviceAccounts = await dbContext.ServiceAccounts.Where(s => s.IsActive).OrderBy(s => s.Name)
            .Select(s => new GrantablePrincipal { Type = "service-accounts", Id = s.Id, Name = s.Name })
            .ToListAsync(cancellationToken);

        var principals = new List<GrantablePrincipal>([.. groups, .. users, .. serviceAccounts]);

        // The address at which a grant FOR THIS PRINCIPAL is written (issue #416). A new grant has no resource
        // yet, so there is nothing else that could carry its address — putting it on the picker's own rows is
        // what lets the dialog save without composing /acl-entries/{type}/{id} from the selection.
        foreach (var principal in principals)
        {
            principal.Links = [new Link("grant", $"/api/documents/{documentId}/acl-entries/{principal.Type}/{principal.Id}", "PUT")];
        }

        return Ok(new GrantablePrincipalsResource { Principals = principals });
    }

    // Standing convention: every GET action gets a companion HEAD action of its own.
    [HttpHead("grantable-principals")]
    public async Task<IActionResult> HeadGrantablePrincipals(Guid documentId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return await access.CanManagePermissionsAsync(documentId, cancellationToken) ? NoContent() : Forbid();
    }
}

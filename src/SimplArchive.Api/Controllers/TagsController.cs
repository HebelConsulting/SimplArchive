using System.Text.RegularExpressions;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Tags;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The tenant's tag catalog (ADR "Tag controlled vocabulary") — the admin-managed vocabulary behind the free-form
/// document tags. GET lists the active catalog (name + colour) for the tag-editor autocomplete + chip colours (any
/// authenticated caller). The mutating operations (create / rename+recolour / retire / merge) are tenant-admin
/// only; rename + merge cascade-update the DocumentTag strings and re-index the affected documents.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tags")]
[Authorize]
public partial class TagsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IDocumentIndexQueue _queue;

    public TagsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        IDocumentIndexQueue queue)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _queue = queue;
    }

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColorRegex();

    public class TagResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Color { get; set; }
    }

    public class TagsResource : HypermediaResource
    {
        // The active catalog entries. `Tags` is the name-only list (backward-compatible with the old autocomplete
        // shape); `Catalog` adds id + colour.
        public List<string> Tags { get; set; } = [];
        public List<TagResource> Catalog { get; set; } = [];
        public bool CanManage { get; set; }
    }

    public class CreateTagRequest { public string Name { get; set; } = string.Empty; public string? Color { get; set; } }
    public class UpdateTagRequest { public string? Name { get; set; } public string? Color { get; set; } }
    public class MergeTagRequest { public Guid IntoId { get; set; } }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var catalog = await _dbContext.TagDefinitions
            .Where(t => t.RetiredAt == null)
            .OrderBy(t => t.Name)
            .Select(t => new TagResource { Id = t.Id, Name = t.Name, Color = t.Color })
            .ToListAsync(cancellationToken);

        // Each catalog row addresses itself: rename/recolour (PUT), retire (DELETE) and merge-into-another
        // (POST). Only the LIVE tags are listed here, so `unretire` has no row to hang off and is not offered
        // (issue #416).
        foreach (var tag in catalog)
        {
            tag.Links =
            [
                new Link("self", $"/api/tags/{tag.Id}", "PUT"),
                new Link("retire", $"/api/tags/{tag.Id}", "DELETE"),
                new Link("merge", $"/api/tags/{tag.Id}/merge", "POST"),
            ];
        }

        return Ok(new TagsResource
        {
            Tags = catalog.Select(t => t.Name).ToList(),
            Catalog = catalog,
            CanManage = await IsTenantAdminAsync(cancellationToken),
            Links = [new Link("self", "/api/tags", "GET")],
        });
    }

    [HttpHead]
    public IActionResult Head() => NoContent();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateTagRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var name = Normalize(request.Name);
        ValidateName(name);
        ValidateColor(request.Color);

        if (await _dbContext.TagDefinitions.AnyAsync(t => t.Name == name, cancellationToken))
        {
            throw new TagNameConflictException();
        }

        var tag = new TagDefinition { Id = Guid.NewGuid(), TenantId = tenantId, Name = name, Color = request.Color, CreatedAt = DateTimeOffset.UtcNow };
        _dbContext.TagDefinitions.Add(tag);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new TagResource { Id = tag.Id, Name = tag.Name, Color = tag.Color });
    }

    // Rename (cascades the DocumentTag strings + reindex) and/or recolour a catalog tag.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTagRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tag = await _dbContext.TagDefinitions.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tag is null)
        {
            throw new TagNotFoundException();
        }

        if (request.Color is not null || (request.Name is null && request.Color is null))
        {
            ValidateColor(request.Color);
            tag.Color = string.IsNullOrEmpty(request.Color) ? null : request.Color;
        }

        if (request.Name is not null)
        {
            var newName = Normalize(request.Name);
            ValidateName(newName);
            if (newName != tag.Name)
            {
                if (await _dbContext.TagDefinitions.AnyAsync(t => t.Name == newName && t.Id != id, cancellationToken))
                {
                    throw new TagNameConflictException();
                }

                await RetagAsync(tag.Name, newName, cancellationToken);
                tag.Name = newName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return Ok(new TagResource { Id = tag.Id, Name = tag.Name, Color = tag.Color });
    }

    // Retire (soft — DELETE) / un-retire a catalog tag. Existing usages on documents are grandfathered.
    [HttpDelete("{id:guid}")]
    public Task<IActionResult> Retire(Guid id, CancellationToken cancellationToken) => SetRetiredAsync(id, DateTimeOffset.UtcNow, cancellationToken);

    [HttpPost("{id:guid}/unretire")]
    public Task<IActionResult> Unretire(Guid id, CancellationToken cancellationToken) => SetRetiredAsync(id, null, cancellationToken);

    // Fold this catalog tag into another: re-tag every document that has it, then remove this definition.
    [HttpPost("{id:guid}/merge")]
    public async Task<IActionResult> Merge(Guid id, [FromBody] MergeTagRequest request, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        if (request.IntoId == id)
        {
            throw new InvalidTagException("A tag cannot be merged into itself.");
        }

        var source = await _dbContext.TagDefinitions.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        var target = await _dbContext.TagDefinitions.SingleOrDefaultAsync(t => t.Id == request.IntoId, cancellationToken);
        if (source is null || target is null)
        {
            throw new TagNotFoundException();
        }

        await RetagAsync(source.Name, target.Name, cancellationToken);
        _dbContext.TagDefinitions.Remove(source);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task<IActionResult> SetRetiredAsync(Guid id, DateTimeOffset? retiredAt, CancellationToken cancellationToken)
    {
        if (!await IsTenantAdminAsync(cancellationToken))
        {
            return Forbid();
        }

        var tag = await _dbContext.TagDefinitions.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (tag is null)
        {
            throw new TagNotFoundException();
        }

        tag.RetiredAt = retiredAt;
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    // Re-points every DocumentTag row with `oldName` to `newName`, deduping per document (a document that already
    // has newName drops the old row instead of colliding on the unique index), then re-indexes the affected docs.
    private async Task RetagAsync(string oldName, string newName, CancellationToken cancellationToken)
    {
        var affected = await _dbContext.DocumentTags.Where(t => t.Tag == oldName).ToListAsync(cancellationToken);
        if (affected.Count == 0)
        {
            return;
        }

        var docIds = affected.Select(t => t.DocumentId).Distinct().ToList();
        var alreadyHaveNew = (await _dbContext.DocumentTags
            .Where(t => t.Tag == newName && docIds.Contains(t.DocumentId))
            .Select(t => t.DocumentId)
            .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var row in affected)
        {
            if (alreadyHaveNew.Contains(row.DocumentId))
            {
                _dbContext.DocumentTags.Remove(row); // the document already carries the target tag
            }
            else
            {
                row.Tag = newName;
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _queue.EnqueueManyAsync(docIds, cancellationToken);
    }

    private static string Normalize(string name) => (name ?? "").Trim().ToLowerInvariant();

    private static void ValidateName(string name)
    {
        if (name.Length is 0 or > 100)
        {
            throw new InvalidTagException("A tag name must be 1–100 characters.");
        }
    }

    private void ValidateColor(string? color)
    {
        if (!string.IsNullOrEmpty(color) && !HexColorRegex().IsMatch(color))
        {
            throw new InvalidTagException("A tag colour must be a #RRGGBB hex value.");
        }
    }

    private async Task<bool> IsTenantAdminAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
        && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).IsTenantAdmin;
}

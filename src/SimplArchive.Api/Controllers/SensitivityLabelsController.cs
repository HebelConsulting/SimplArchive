using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Documents;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The tenant's configurable data-classification labels (ADR "Configurable sensitivity labels + upload defaults",
/// superseding the fixed enum of ADR 0399). <c>GET</c> is the picker catalog for any authenticated caller;
/// create/update/retire are gated on <c>CanManageClassification</c>. A rename re-indexes the affected documents so
/// the search facet/filter reflect the new name. A small bounded catalog, so not paginated.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/sensitivity-labels")]
[Authorize]
public class SensitivityLabelsController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;
    private readonly IUserSystemRightsResolver _userSystemRights;
    private readonly IDocumentIndexQueue _indexQueue;

    public SensitivityLabelsController(
        SimplArchiveDbContext dbContext,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor,
        IUserSystemRightsResolver userSystemRights,
        IDocumentIndexQueue indexQueue)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
        _userSystemRights = userSystemRights;
        _indexQueue = indexQueue;
    }

    public class SensitivityLabelResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public string? Color { get; set; }
        public bool Watermark { get; set; }
        public bool Retired { get; set; }
    }

    public class SensitivityLabelsResource : HypermediaResource
    {
        public List<SensitivityLabelResource> Labels { get; set; } = [];
        // Whether the caller may create/edit/retire labels (CanManageClassification) — gates the admin UI.
        public bool CanManage { get; set; }
    }

    public class UpsertSensitivityLabelRequest
    {
        public string Name { get; set; } = "";
        public int Rank { get; set; }
        public string? Color { get; set; }
        public bool Watermark { get; set; }
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var labels = await _dbContext.SensitivityLabelDefinitions
            .OrderBy(l => l.Rank).ThenBy(l => l.Name)
            .Select(l => new SensitivityLabelResource
            {
                Id = l.Id,
                Name = l.Name,
                Rank = l.Rank,
                Color = l.Color,
                Watermark = l.Watermark,
                Retired = l.RetiredAt != null,
            })
            .ToListAsync(cancellationToken);

        return Ok(new SensitivityLabelsResource
        {
            Labels = labels,
            CanManage = await CanManageAsync(cancellationToken),
            Links = [new Link("self", "/api/sensitivity-labels", "GET")],
        });
    }

    [HttpHead]
    public IActionResult Head() => NoContent();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertSensitivityLabelRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken) || _currentTenantAccessor.TenantId is not { } tenantId)
        {
            return Forbid();
        }

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new InvalidSensitivityLabelException();
        }

        var label = new SensitivityLabelDefinition
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Rank = request.Rank,
            Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim(),
            Watermark = request.Watermark,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.SensitivityLabelDefinitions.Add(label);
        await SaveOrConflictAsync(cancellationToken);

        return CreatedAtAction(nameof(List), ToResource(label));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertSensitivityLabelRequest request, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken))
        {
            return Forbid();
        }

        var label = await _dbContext.SensitivityLabelDefinitions.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (label is null)
        {
            throw new SensitivityLabelNotFoundException();
        }

        var name = (request.Name ?? "").Trim();
        if (name.Length == 0)
        {
            throw new InvalidSensitivityLabelException();
        }

        var renamed = !string.Equals(label.Name, name, StringComparison.Ordinal);
        label.Name = name;
        label.Rank = request.Rank;
        label.Color = string.IsNullOrWhiteSpace(request.Color) ? null : request.Color.Trim();
        label.Watermark = request.Watermark;
        await SaveOrConflictAsync(cancellationToken);

        // A rename changes the indexed keyword for every document carrying this label → reindex them so the
        // search facet/filter stay correct.
        if (renamed)
        {
            var docIds = await _dbContext.Documents.Where(d => d.SensitivityLabelId == id).Select(d => d.Id).ToListAsync(cancellationToken);
            if (docIds.Count > 0)
            {
                await _indexQueue.EnqueueManyAsync(docIds, cancellationToken);
            }
        }

        return Ok(ToResource(label));
    }

    // Retire (soft): keeps the label on existing documents, stops offering it for new classification.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Retire(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken))
        {
            return Forbid();
        }

        var label = await _dbContext.SensitivityLabelDefinitions.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (label is null)
        {
            return NotFound();
        }

        if (label.RetiredAt is null)
        {
            label.RetiredAt = DateTimeOffset.UtcNow;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/unretire")]
    public async Task<IActionResult> Unretire(Guid id, CancellationToken cancellationToken)
    {
        if (!await CanManageAsync(cancellationToken))
        {
            return Forbid();
        }

        var label = await _dbContext.SensitivityLabelDefinitions.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (label is null)
        {
            throw new SensitivityLabelNotFoundException();
        }

        if (label.RetiredAt is not null)
        {
            label.RetiredAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        return Ok(ToResource(label));
    }

    private static SensitivityLabelResource ToResource(SensitivityLabelDefinition l) => new()
    {
        Id = l.Id,
        Name = l.Name,
        Rank = l.Rank,
        Color = l.Color,
        Watermark = l.Watermark,
        Retired = l.RetiredAt != null,

        // Retire and un-retire are mutually exclusive by construction, so the pair is advertised as state rather
        // than as two buttons the client greys out from a `Retired` flag it interprets itself (issue #416): a
        // live label offers `retire`, a retired one offers `unretire`, and neither client has to know the paths.
        Links =
        [
            new Link("self", $"/api/sensitivity-labels/{l.Id}", "PUT"),
            l.RetiredAt is null
                ? new Link("retire", $"/api/sensitivity-labels/{l.Id}", "DELETE")
                : new Link("unretire", $"/api/sensitivity-labels/{l.Id}/unretire", "POST"),
        ],
    };

    private async Task SaveOrConflictAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            throw new SensitivityLabelNameConflictException(); // the (TenantId, Name) unique index
        }
    }

    private async Task<bool> CanManageAsync(CancellationToken cancellationToken) =>
        _currentUserAccessor.UserId is { } userId
            && (await _userSystemRights.GetEffectiveSystemRightsAsync(userId, cancellationToken)).CanManageClassification;
}

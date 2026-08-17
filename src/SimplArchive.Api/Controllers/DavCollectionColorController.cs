using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.CalDav;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's own colour for a typed collection (#564 slice 2, ADR 0620). The collection's DEFAULT colour is
/// an ordinary index field on the folder and is edited like any other index data; this is the personal override
/// on top of it, which is why it lives on its own sub-resource and needs no rights beyond seeing the folder.
/// </summary>
/// <remarks>
/// DELETE is the reset: absence of a row is what makes the collection's default apply, so there is no "unset"
/// value to write. A caller who can see the collection may colour it — a preference about how THEY see a folder
/// is not a change to the folder.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/collection-color")]
[Authorize]
public class DavCollectionColorController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly Documents.DocumentAccessService _access;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly ICurrentTenantAccessor _currentTenantAccessor;

    public DavCollectionColorController(
        SimplArchiveDbContext dbContext,
        Documents.DocumentAccessService access,
        ICurrentUserAccessor currentUserAccessor,
        ICurrentTenantAccessor currentTenantAccessor)
    {
        _dbContext = dbContext;
        _access = access;
        _currentUserAccessor = currentUserAccessor;
        _currentTenantAccessor = currentTenantAccessor;
    }

    public class SetColorRequest
    {
        /// <summary>A CSS colour as the client writes it (e.g. <c>#3f51b5</c>).</summary>
        public string Color { get; set; } = string.Empty;
    }

    [HttpPut]
    public async Task<IActionResult> Set(Guid documentId, [FromBody] SetColorRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (request.Color is not { Length: > 0 and <= 32 })
        {
            return BadRequest();
        }

        if (!(await _access.GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
        {
            return NotFound();
        }

        var existing = await _dbContext.DavCollectionColors
            .FirstOrDefaultAsync(c => c.UserId == userId && c.DocumentId == documentId, cancellationToken);
        if (existing is null)
        {
            _dbContext.DavCollectionColors.Add(new DavCollectionColor
            {
                UserId = userId,
                DocumentId = documentId,
                TenantId = _currentTenantAccessor.TenantId!.Value,
                Color = request.Color,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            existing.Color = request.Color;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Reset(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var existing = await _dbContext.DavCollectionColors
            .FirstOrDefaultAsync(c => c.UserId == userId && c.DocumentId == documentId, cancellationToken);
        if (existing is not null)
        {
            _dbContext.DavCollectionColors.Remove(existing);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }

        // Idempotent: with no override the collection default already applies, which is what a reset asks for.
        return NoContent();
    }
}

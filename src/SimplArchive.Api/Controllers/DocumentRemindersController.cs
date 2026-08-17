using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors.Exceptions.Reminders;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Documents;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// Document reminders — see ADR "Document reminders". A user sets a reminder on a
/// document for a future date (optionally targeting a colleague, optionally recurring); a background sweep
/// notifies the target on the due date. Reading/creating requires <c>CanSee</c> on the document; the target
/// (if not the caller) must be an active user who can also see it. User-only (a ServiceAccount has no intray).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/reminders")]
[Authorize]
public class DocumentRemindersController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public DocumentRemindersController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentUserAccessor = currentUserAccessor;
    }

    public class ReminderResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public Guid DocumentId { get; set; }
        public Guid TargetUserId { get; set; }
        public string TargetName { get; set; } = "";
        public DateTimeOffset RemindAt { get; set; }
        public string? Note { get; set; }
        public int Recurrence { get; set; }
        public string RecurrenceName { get; set; } = "";
        public string CreatedByName { get; set; } = "";
        public bool Mine { get; set; }
    }

    public class ReminderListResource : HypermediaResource
    {
        public List<ReminderResource> Reminders { get; set; } = [];
    }

    public class ReminderTargetResource
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; } = "";
    }

    public class ReminderTargetsResource : HypermediaResource
    {
        public List<ReminderTargetResource> Targets { get; set; } = [];
    }

    public class CreateReminderRequest
    {
        public DateTimeOffset RemindAt { get; set; }
        public string? Note { get; set; }
        public int Recurrence { get; set; }
        public Guid? TargetUserId { get; set; }
    }

    // The caller's reminders on this document — ones they set or are the target of (pending; a fired one-shot
    // has FiredAt set and is excluded).
    [HttpGet]
    public async Task<IActionResult> List(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        var reminders = await _dbContext.DocumentReminders
            .Where(r => r.DocumentId == documentId && r.FiredAt == null && (r.UserId == userId || r.CreatedByUserId == userId))
            .OrderBy(r => r.RemindAt)
            .Select(r => new
            {
                r.Id,
                r.DocumentId,
                r.UserId,
                TargetName = _dbContext.Users.Where(u => u.Id == r.UserId).Select(u => u.DisplayName).FirstOrDefault(),
                r.RemindAt,
                r.Note,
                r.Recurrence,
                CreatedByName = _dbContext.Users.Where(u => u.Id == r.CreatedByUserId).Select(u => u.DisplayName).FirstOrDefault(),
                Mine = r.CreatedByUserId == userId || r.UserId == userId,
            })
            .ToListAsync(cancellationToken);

        return Ok(new ReminderListResource
        {
            Reminders = [.. reminders.Select(r => new ReminderResource
            {
                Id = r.Id,
                DocumentId = r.DocumentId,
                TargetUserId = r.UserId,
                TargetName = r.TargetName ?? "Unknown",
                RemindAt = r.RemindAt,
                Note = r.Note,
                Recurrence = (int)r.Recurrence,
                RecurrenceName = r.Recurrence.ToString(),
                CreatedByName = r.CreatedByName ?? "Unknown",
                Mine = r.Mine,
                Links = [new Link("cancel", $"/api/documents/{documentId}/reminders/{r.Id}", "DELETE")],
            })],
            Links =
            [
                new Link("self", $"/api/documents/{documentId}/reminders", "GET"),
                // Setting one, and the people it may be set for (issue #416). Both hang off the collection the
                // dialog already has open, so it follows them instead of rebuilding two more paths from an id.
                new Link("create", $"/api/documents/{documentId}/reminders", "POST"),
                new Link("targets", $"/api/documents/{documentId}/reminders/targets", "GET"),
            ],
        });
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return (await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee ? NoContent() : Forbid();
    }

    // Active tenant users the caller can assign a reminder to (the target picker). CanSee-gated — anyone who
    // can see the document can set a reminder on it and pick a target.
    [HttpGet("targets")]
    public async Task<IActionResult> Targets(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        var targets = await _dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName)
            .Select(u => new ReminderTargetResource { Id = u.Id, DisplayName = u.DisplayName })
            .ToListAsync(cancellationToken);

        return Ok(new ReminderTargetsResource
        {
            Targets = targets,
            Links = [new Link("self", $"/api/documents/{documentId}/reminders/targets", "GET")],
        });
    }

    [HttpHead("targets")]
    public async Task<IActionResult> TargetsHead(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        if (!await _dbContext.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return NotFound();
        }

        return (await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee ? NoContent() : Forbid();
    }

    [HttpPost]
    public async Task<IActionResult> Create(Guid documentId, [FromBody] CreateReminderRequest request, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var tenantId = await _dbContext.Documents
            .Where(d => d.Id == documentId)
            .Select(d => (Guid?)d.TenantId)
            .SingleOrDefaultAsync(cancellationToken);
        if (tenantId is null)
        {
            return NotFound();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        if (request.RemindAt <= DateTimeOffset.UtcNow)
        {
            throw new ReminderInPastException();
        }

        if (!Enum.IsDefined((ReminderRecurrence)request.Recurrence))
        {
            throw new InvalidRecurrenceException();
        }

        var targetId = request.TargetUserId ?? userId;
        if (targetId != userId)
        {
            // The target must be an active user in the tenant who can also see the document.
            var targetActive = await _dbContext.Users.AnyAsync(u => u.Id == targetId && u.IsActive, cancellationToken);
            if (!targetActive || !(await _effectiveRightsCalculator.GetEffectiveRightsAsync(targetId, documentId, cancellationToken)).CanSee)
            {
                throw new InvalidReminderTargetException();
            }
        }

        var reminder = new DocumentReminder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId.Value,
            UserId = targetId,
            DocumentId = documentId,
            RemindAt = request.RemindAt,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            Recurrence = (ReminderRecurrence)request.Recurrence,
            CreatedByUserId = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _dbContext.DocumentReminders.Add(reminder);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return StatusCode(StatusCodes.Status201Created, new ReminderResource
        {
            Id = reminder.Id,
            DocumentId = documentId,
            TargetUserId = targetId,
            TargetName = await _dbContext.Users.Where(u => u.Id == targetId).Select(u => u.DisplayName).SingleAsync(cancellationToken),
            RemindAt = reminder.RemindAt,
            Note = reminder.Note,
            Recurrence = (int)reminder.Recurrence,
            RecurrenceName = reminder.Recurrence.ToString(),
            CreatedByName = await _dbContext.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(cancellationToken),
            Mine = true,
            Links = [new Link("cancel", $"/api/documents/{documentId}/reminders/{reminder.Id}", "DELETE")],
        });
    }

    // Cancel a reminder — the creator or the target may cancel it.
    [HttpDelete("{reminderId:guid}")]
    public async Task<IActionResult> Cancel(Guid documentId, Guid reminderId, CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var reminder = await _dbContext.DocumentReminders
            .SingleOrDefaultAsync(r => r.Id == reminderId && r.DocumentId == documentId, cancellationToken);
        if (reminder is null)
        {
            return NotFound();
        }

        if (reminder.UserId != userId && reminder.CreatedByUserId != userId)
        {
            return Forbid();
        }

        _dbContext.DocumentReminders.Remove(reminder);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }
}

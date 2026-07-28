using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The caller's cross-document reminders (ADR "My work dashboard") — the overdue + due-soon pending reminders
/// they set or are the target of, for the personal dashboard. User-only (a ServiceAccount has no reminders).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/reminders")]
[Authorize]
public class RemindersController : ControllerBase
{
    // The dashboard's "needs attention" window: overdue plus anything due within the next week.
    private static readonly TimeSpan DueSoonWindow = TimeSpan.FromDays(7);

    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public RemindersController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
    }

    public class ReminderResource : HypermediaResource
    {
        public Guid Id { get; set; }
        public Guid DocumentId { get; set; }
        public Guid? ParentId { get; set; }
        public string DocumentName { get; set; } = "";
        public DateTimeOffset RemindAt { get; set; }
        public string? Note { get; set; }
        public int Recurrence { get; set; }
        public string RecurrenceName { get; set; } = "";
        public bool Overdue { get; set; }
    }

    public class ReminderListResource : HypermediaResource
    {
        public List<ReminderResource> Reminders { get; set; } = [];
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (_currentUserAccessor.UserId is not { } userId)
        {
            return Forbid();
        }

        var now = DateTimeOffset.UtcNow;
        var threshold = now + DueSoonWindow;

        // Pending reminders the caller set or targets, joined to the (soft-delete-filtered) document. The due-soon
        // window is applied client-side (SQLite can't translate a DateTimeOffset comparison; the set is small).
        var pending = await _dbContext.DocumentReminders
            .Where(r => r.FiredAt == null && (r.UserId == userId || r.CreatedByUserId == userId))
            .Join(_dbContext.Documents, r => r.DocumentId, d => d.Id, (r, d) => new
            {
                r.Id,
                r.DocumentId,
                d.ParentId,
                DocumentName = d.Name,
                r.RemindAt,
                r.Note,
                r.Recurrence,
            })
            .ToListAsync(cancellationToken);

        var reminders = pending
            .Where(r => r.RemindAt <= threshold)
            .OrderBy(r => r.RemindAt)
            .Select(r => new ReminderResource
            {
                Id = r.Id,
                DocumentId = r.DocumentId,
                ParentId = r.ParentId,
                DocumentName = r.DocumentName,
                RemindAt = r.RemindAt,
                Note = r.Note,
                Recurrence = (int)r.Recurrence,
                RecurrenceName = r.Recurrence.ToString(),
                Overdue = r.RemindAt < now,
                Links = [new Link("document", $"/api/documents/{r.DocumentId}", "GET")],
            })
            .ToList();

        return Ok(new ReminderListResource { Reminders = reminders, Links = [new Link("self", "/api/reminders", "GET")] });
    }

    [HttpHead]
    public IActionResult Head() => _currentUserAccessor.UserId is null ? Forbid() : NoContent();
}

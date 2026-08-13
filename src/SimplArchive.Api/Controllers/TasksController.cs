using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Api.Pagination;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The signed-in user's pending workflow tasks — versions currently In Review and assigned to them (ADR
/// "Workflow / document state model", 0009: a task-based, persistent pending-tasks list). Backs the client's
/// Tasks tab. A ServiceAccount can't be a review assignee, so it always sees an empty list. Soft-deleted
/// (recycle-bin) documents drop out via the Documents query filter. Cursor-paginated by assignment time.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly ICurrentUserAccessor _currentUserAccessor;

    public TasksController(SimplArchiveDbContext dbContext, ICurrentUserAccessor currentUserAccessor)
    {
        _dbContext = dbContext;
        _currentUserAccessor = currentUserAccessor;
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? cursor, [FromQuery] int? limit, CancellationToken cancellationToken)
    {
        var pageSize = PageSize.Resolve(limit);

        if (_currentUserAccessor.UserId is not { } userId)
        {
            // ServiceAccount / no user → no tasks.
            return Ok(new TaskListResource { Tasks = [], Links = [SelfLink(cursor, pageSize)] });
        }

        // Filter/order on the entity BEFORE projecting into the row record — projecting into a positional
        // record first, then ordering/filtering on it, fails EF Core translation (see the same note on
        // RepositoriesController.List, ADR "Blazor repository/document browsing").
        var query =
            from w in _dbContext.WorkflowStates
            where w.Status == WorkflowStatus.InReview && w.AssignedToUserId == userId
            join v in _dbContext.DocumentVersions on w.DocumentVersionId equals v.Id
            join d in _dbContext.Documents on v.DocumentId equals d.Id // soft-delete filtered
            select new { w, v, d };

        if (Cursor.TryDecode(cursor, out var cursorAt, out var cursorId))
        {
            query = query.Where(x => x.w.UpdatedAt > cursorAt || (x.w.UpdatedAt == cursorAt && x.w.Id > cursorId));
        }

        var fetched = await query
            .OrderBy(x => x.w.UpdatedAt).ThenBy(x => x.w.Id)
            .Take(pageSize + 1)
            .Select(x => new TaskRow(x.w.UpdatedAt, x.w.Id, x.d.Id, x.d.ParentId, x.v.Id, x.d.Name, x.v.VersionNumber, x.w.DueAt))
            .ToListAsync(cancellationToken);

        var (page, hasMore) = Cursor.Split(fetched, pageSize);

        var links = new List<Link> { SelfLink(cursor, pageSize) };
        if (hasMore)
        {
            var next = Cursor.Encode(page[^1].AssignedAt, page[^1].StateId);
            links.Add(new Link("next", Url.Action(nameof(List), new { cursor = next, limit = pageSize })!, "GET"));
        }

        var now = DateTimeOffset.UtcNow;
        var tasks = page.Select(r => new TaskResource
        {
            DocumentId = r.DocumentId,
            ParentId = r.ParentId,
            VersionId = r.VersionId,
            DocumentName = r.DocumentName,
            VersionNumber = r.VersionNumber,
            AssignedAt = r.AssignedAt,
            DueAt = r.DueAt,
            IsOverdue = r.DueAt is { } due && now > due,
            // `parent` beside `document` (#443): opening a task navigates to the document's HOME folder, and a
            // row that names a parent id without its address leaves the client an id it can only compose from.
            // Absent for a repository-root document, where "the parent" is the repositories listing itself.
            Links = r.ParentId is { } taskParent
                ?
                [
                    new Link("document", $"/api/documents/{r.DocumentId}", "GET"),
                    new Link("parent", $"/api/documents/{taskParent}", "GET"),
                    new Link("workflow", $"/api/documents/{r.DocumentId}/versions/{r.VersionId}/workflow", "GET"),
                ]
                :
                [
                    new Link("document", $"/api/documents/{r.DocumentId}", "GET"),
                    new Link("workflow", $"/api/documents/{r.DocumentId}/versions/{r.VersionId}/workflow", "GET"),
                ],
        }).ToList();

        return Ok(new TaskListResource { Tasks = tasks, Links = links });
    }

    [HttpHead]
    public IActionResult Head() => NoContent();

    private Link SelfLink(string? cursor, int pageSize) =>
        new("self", Url.Action(nameof(List), new { cursor, limit = pageSize })!, "GET");

    private record TaskRow(DateTimeOffset AssignedAt, Guid StateId, Guid DocumentId, Guid? ParentId, Guid VersionId, string DocumentName, int? VersionNumber, DateTimeOffset? DueAt);

    public class TaskListResource : HypermediaResource
    {
        public List<TaskResource> Tasks { get; set; } = [];
    }

    public class TaskResource : HypermediaResource
    {
        public Guid DocumentId { get; set; }
        public Guid? ParentId { get; set; }
        public Guid VersionId { get; set; }
        public string DocumentName { get; set; } = "";
        public int? VersionNumber { get; set; }
        public DateTimeOffset AssignedAt { get; set; }
        public DateTimeOffset? DueAt { get; set; }
        public bool IsOverdue { get; set; }
    }
}

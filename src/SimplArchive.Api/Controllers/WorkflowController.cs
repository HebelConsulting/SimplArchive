using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Errors;
using SimplArchive.Api.Errors.Exceptions.Checkout;
using SimplArchive.Api.Errors.Exceptions.Workflow;
using SimplArchive.Api.Hypermedia;
using SimplArchive.Application.Abstractions;
using SimplArchive.Domain.Notifications;
using SimplArchive.Domain.Acl;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Workflow;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Controllers;

/// <summary>
/// The fixed approval workflow on a document version (ADR "Workflow / document state model", 0009) — slice 1:
/// the core state machine + task inbox, opt-in (a version has no workflow until submitted), reviewer = a
/// specific User. Transitions are POST action sub-resources (a genuine state change, like restore/rotate-secret
/// under the RESTful-naming convention) surfaced as HATEOAS links only when valid + permitted. Authorization is
/// Document-scope and accepts a ServiceAccount or a logged-in User caller — but approve/reject require being the
/// assigned reviewer, which is always a User. Transitions record an audit event (ADR 0315) and an in-app
/// notification (ADR "Notifications (in-app, first slice)"). Escalation/SLA, deactivation-reassignment, and the
/// checkout interaction are deferred to later slices.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/documents/{documentId:guid}/versions/{versionId:guid}/workflow")]
[Authorize]
public class WorkflowController : ControllerBase
{
    private readonly SimplArchiveDbContext _dbContext;
    private readonly IEffectiveRightsCalculator _effectiveRightsCalculator;
    private readonly ICurrentServiceAccountAccessor _currentServiceAccountAccessor;
    private readonly ICurrentUserAccessor _currentUserAccessor;
    private readonly IAuditRecorder _audit;
    private readonly INotificationService _notifications;

    public WorkflowController(
        SimplArchiveDbContext dbContext,
        IEffectiveRightsCalculator effectiveRightsCalculator,
        ICurrentServiceAccountAccessor currentServiceAccountAccessor,
        ICurrentUserAccessor currentUserAccessor,
        IAuditRecorder audit,
        INotificationService notifications)
    {
        _dbContext = dbContext;
        _effectiveRightsCalculator = effectiveRightsCalculator;
        _currentServiceAccountAccessor = currentServiceAccountAccessor;
        _currentUserAccessor = currentUserAccessor;
        _audit = audit;
        _notifications = notifications;
    }

    private Task<string?> DocumentNameAsync(Guid documentId, CancellationToken cancellationToken) =>
        _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken);

    // ---- Read -------------------------------------------------------------------------------------------

    [HttpGet]
    public async Task<IActionResult> Get(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        return Ok(await BuildResourceAsync(documentId, version, rights, cancellationToken));
    }

    [HttpHead]
    public async Task<IActionResult> Head(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        return (await GetCallerRightsAsync(documentId, cancellationToken)).CanSee ? NoContent() : Forbid();
    }

    // ---- Transitions ------------------------------------------------------------------------------------

    // Draft (no row) or Rejected → In Review, assigning a specific User reviewer. Requires CanEditContent; the
    // reviewer must be an active tenant User with CanReadContent on the document (they can't review what they
    // can't read).
    [HttpPost("submit")]
    public async Task<IActionResult> Submit(Guid documentId, Guid versionId, [FromBody] SubmitRequest request, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        if (version.Status != DocumentVersionStatus.Confirmed)
        {
            throw new VersionNotConfirmedException();
        }

        // A checked-out document is mid-edit (a check-in will supersede the version being submitted), so it can't
        // be submitted for review until the check-out is resolved (ADR "Workflow / check-out interaction") —
        // blocked regardless of who holds the lock, including the submitter. Check-out *during* review stays
        // allowed (a new version doesn't touch the reviewed one).
        if (await _dbContext.Documents.Where(d => d.Id == documentId).Select(d => d.CheckedOutByUserId).FirstOrDefaultAsync(cancellationToken) is not null)
        {
            throw DocumentCheckedOutException.ForSubmit();
        }

        var reviewer = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.ReviewerId, cancellationToken);
        if (reviewer is null || !reviewer.IsActive)
        {
            throw InvalidReviewerException.NotActive();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(reviewer.Id, documentId, cancellationToken)).CanReadContent)
        {
            throw InvalidReviewerException.CannotReadContent();
        }

        var state = await LoadStateAsync(versionId, cancellationToken);
        var from = state?.Status ?? WorkflowStatus.Draft;
        if (from is not (WorkflowStatus.Draft or WorkflowStatus.Rejected))
        {
            throw InvalidTransition(from, "submit");
        }

        var now = DateTimeOffset.UtcNow;

        // Review deadline from the document's mask SLA (ADR "Workflow escalation / SLA reminders"): null when
        // the document has no mask, or the mask defines no ReviewSlaDays.
        var slaDays = await _dbContext.Documents
            .Where(d => d.Id == documentId && d.MaskVersionId != null)
            .Select(d => _dbContext.MaskVersions.Where(mv => mv.Id == d.MaskVersionId).Select(mv => mv.ReviewSlaDays).FirstOrDefault())
            .FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset? dueAt = slaDays is { } days ? now.AddDays(days) : null;

        if (state is null)
        {
            state = new WorkflowState
            {
                Id = Guid.NewGuid(),
                TenantId = version.TenantId,
                DocumentVersionId = versionId,
                Status = WorkflowStatus.InReview,
                AssignedToUserId = reviewer.Id,
                CreatedAt = now,
                UpdatedAt = now,
                DueAt = dueAt,
            };
            _dbContext.WorkflowStates.Add(state);
        }
        else
        {
            state.Status = WorkflowStatus.InReview;
            state.AssignedToUserId = reviewer.Id;
            state.UpdatedAt = now;
            // A resubmit gets a fresh deadline + clears the sweep bookkeeping.
            state.DueAt = dueAt;
            state.ReminderSentAt = null;
            state.EscalatedAt = null;
        }

        AddTransition(state, from, WorkflowStatus.InReview, assignedToUserId: reviewer.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var docName = await DocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.WorkflowSubmitted, "Document", documentId, docName, $"reviewer: {reviewer.DisplayName}", cancellationToken: cancellationToken);
        await _notifications.NotifyAsync(reviewer.Id, NotificationType.ReviewAssigned, "Review requested", $"You've been asked to review '{docName}'.", documentId, cancellationToken);

        var version2 = await LoadVersionAsync(documentId, versionId, cancellationToken);
        return Ok(await BuildResourceAsync(documentId, version2!, await GetCallerRightsAsync(documentId, cancellationToken), cancellationToken));
    }

    // In Review → Approved. Only the assigned reviewer can act.
    [HttpPost("approve")]
    public Task<IActionResult> Approve(Guid documentId, Guid versionId, CancellationToken cancellationToken) =>
        ReviewerDecisionAsync(documentId, versionId, WorkflowStatus.Approved, reason: null, cancellationToken);

    // In Review → Rejected, with a mandatory non-blank reason (ADR 0143). Only the assigned reviewer can act.
    [HttpPost("reject")]
    public Task<IActionResult> Reject(Guid documentId, Guid versionId, [FromBody] RejectRequest request, CancellationToken cancellationToken) =>
        ReviewerDecisionAsync(documentId, versionId, WorkflowStatus.Rejected, request.Reason, cancellationToken);

    private async Task<IActionResult> ReviewerDecisionAsync(Guid documentId, Guid versionId, WorkflowStatus to, string? reason, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        // Must be able to see the document at all, then be the assigned reviewer.
        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanSee)
        {
            return Forbid();
        }

        var state = await LoadStateAsync(versionId, cancellationToken);
        if (state is null || state.Status != WorkflowStatus.InReview)
        {
            throw InvalidTransition(state?.Status ?? WorkflowStatus.Draft, to == WorkflowStatus.Approved ? "approve" : "reject");
        }

        if (_currentUserAccessor.UserId is not { } userId || state.AssignedToUserId != userId)
        {
            return Forbid(); // only the assigned reviewer decides
        }

        if (to == WorkflowStatus.Rejected && string.IsNullOrWhiteSpace(reason))
        {
            throw new RejectionReasonRequiredException();
        }

        state.Status = to;
        state.AssignedToUserId = null; // resolved — no longer a pending task
        state.UpdatedAt = DateTimeOffset.UtcNow;
        AddTransition(state, WorkflowStatus.InReview, to, rejectionReason: to == WorkflowStatus.Rejected ? reason!.Trim() : null);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var docName = await DocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(to == WorkflowStatus.Approved ? AuditActions.WorkflowApproved : AuditActions.WorkflowRejected,
            "Document", documentId, docName,
            to == WorkflowStatus.Rejected ? $"reason: {reason!.Trim()}" : null, cancellationToken: cancellationToken);

        // Notify whoever submitted it for review of the reviewer's decision.
        if (await SubmitterUserIdAsync(state.Id, cancellationToken) is { } submitterId)
        {
            var (type, body) = to == WorkflowStatus.Approved
                ? (NotificationType.WorkflowApproved, $"'{docName}' was approved.")
                : (NotificationType.WorkflowRejected, $"'{docName}' was rejected: {reason!.Trim()}");
            await _notifications.NotifyAsync(submitterId, type, to == WorkflowStatus.Approved ? "Document approved" : "Document rejected", body, documentId, cancellationToken);
        }

        var version2 = await LoadVersionAsync(documentId, versionId, cancellationToken);
        return Ok(await BuildResourceAsync(documentId, version2!, await GetCallerRightsAsync(documentId, cancellationToken), cancellationToken));
    }

    // The performer of the most recent Submit (→ In Review) for this workflow state — the person who requested
    // the review, notified of the outcome. Null when the submitter was a ServiceAccount (no in-app intray).
    private Task<Guid?> SubmitterUserIdAsync(Guid workflowStateId, CancellationToken cancellationToken) =>
        _dbContext.WorkflowTransitions
            .Where(t => t.WorkflowStateId == workflowStateId && t.ToStatus == WorkflowStatus.InReview)
            .OrderByDescending(t => t.CreatedAt)
            .Select(t => t.PerformedByUserId)
            .FirstOrDefaultAsync(cancellationToken);

    // Approved → Released. Requires CanEditContent (the content owner finalizes).
    [HttpPost("release")]
    public async Task<IActionResult> Release(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        if (!(await GetCallerRightsAsync(documentId, cancellationToken)).CanEditContent)
        {
            return Forbid();
        }

        var state = await LoadStateAsync(versionId, cancellationToken);
        if (state is null || state.Status != WorkflowStatus.Approved)
        {
            throw InvalidTransition(state?.Status ?? WorkflowStatus.Draft, "release");
        }

        state.Status = WorkflowStatus.Released;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        AddTransition(state, WorkflowStatus.Approved, WorkflowStatus.Released);
        await _dbContext.SaveChangesAsync(cancellationToken);
        var docName = await DocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.WorkflowReleased, "Document", documentId, docName, cancellationToken: cancellationToken);

        var releaseSubmitterId = await SubmitterUserIdAsync(state.Id, cancellationToken);
        if (releaseSubmitterId is { } submitterId)
        {
            await _notifications.NotifyAsync(submitterId, NotificationType.WorkflowReleased, "Document released", $"'{docName}' was released.", documentId, cancellationToken);
        }

        // Notify everyone following the document (ADR "Document subscriptions"), except the actor and the
        // submitter just notified above.
        await _notifications.NotifyDocumentSubscribersAsync(documentId, NotificationType.SubscribedActivity,
            "Document released", $"'{docName}' was released.",
            releaseSubmitterId is { } s ? [s] : null, cancellationToken);

        var version2 = await LoadVersionAsync(documentId, versionId, cancellationToken);
        return Ok(await BuildResourceAsync(documentId, version2!, await GetCallerRightsAsync(documentId, cancellationToken), cancellationToken));
    }

    // In Review → In Review, handing the pending task to a different reviewer (ADR "Workflow review
    // reassignment") — a delegate/re-route action. Allowed for the currently-assigned reviewer (delegating
    // their own review) or a CanEditContent holder (re-routing). The status is unchanged; only the assignee
    // moves. The same operation the deactivation flow performs in bulk (UsersController.Deactivate).
    [HttpPost("reassign")]
    public async Task<IActionResult> Reassign(Guid documentId, Guid versionId, [FromBody] ReassignRequest request, CancellationToken cancellationToken)
    {
        var version = await LoadVersionAsync(documentId, versionId, cancellationToken);
        if (version is null)
        {
            return NotFound();
        }

        var rights = await GetCallerRightsAsync(documentId, cancellationToken);
        if (!rights.CanSee)
        {
            return Forbid();
        }

        var state = await LoadStateAsync(versionId, cancellationToken);
        if (state is null || state.Status != WorkflowStatus.InReview)
        {
            throw InvalidTransition(state?.Status ?? WorkflowStatus.Draft, "reassign");
        }

        // The assigned reviewer may delegate their own review; anyone with CanEditContent may re-route it.
        var isReviewer = _currentUserAccessor.UserId is { } me && state.AssignedToUserId == me;
        if (!isReviewer && !rights.CanEditContent)
        {
            return Forbid();
        }

        if (state.AssignedToUserId == request.ReviewerId)
        {
            throw InvalidReviewerException.AlreadyAssigned();
        }

        var reviewer = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == request.ReviewerId, cancellationToken);
        if (reviewer is null || !reviewer.IsActive)
        {
            throw InvalidReviewerException.NotActive();
        }

        if (!(await _effectiveRightsCalculator.GetEffectiveRightsAsync(reviewer.Id, documentId, cancellationToken)).CanReadContent)
        {
            throw InvalidReviewerException.CannotReadContent();
        }

        state.AssignedToUserId = reviewer.Id;
        state.UpdatedAt = DateTimeOffset.UtcNow;
        state.ReminderSentAt = null; // the new reviewer gets a fresh pre-deadline reminder; DueAt (the document's) stands
        AddTransition(state, WorkflowStatus.InReview, WorkflowStatus.InReview, assignedToUserId: reviewer.Id);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var docName = await DocumentNameAsync(documentId, cancellationToken);
        await _audit.RecordAsync(AuditActions.WorkflowReassigned, "Document", documentId, docName, $"reviewer: {reviewer.DisplayName}", cancellationToken: cancellationToken);
        await _notifications.NotifyAsync(reviewer.Id, NotificationType.ReviewAssigned, "Review requested", $"You've been asked to review '{docName}'.", documentId, cancellationToken);

        var version2 = await LoadVersionAsync(documentId, versionId, cancellationToken);
        return Ok(await BuildResourceAsync(documentId, version2!, await GetCallerRightsAsync(documentId, cancellationToken), cancellationToken));
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private Task<DocumentVersion?> LoadVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken) =>
        _dbContext.DocumentVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.DocumentId == documentId, cancellationToken);

    private Task<WorkflowState?> LoadStateAsync(Guid versionId, CancellationToken cancellationToken) =>
        _dbContext.WorkflowStates.FirstOrDefaultAsync(w => w.DocumentVersionId == versionId, cancellationToken);

    private void AddTransition(WorkflowState state, WorkflowStatus from, WorkflowStatus to, string? rejectionReason = null, Guid? assignedToUserId = null)
    {
        var (userId, serviceAccountId) = GetCallerIdentity();
        _dbContext.WorkflowTransitions.Add(new WorkflowTransition
        {
            Id = Guid.NewGuid(),
            TenantId = state.TenantId,
            WorkflowStateId = state.Id,
            FromStatus = from,
            ToStatus = to,
            RejectionReason = rejectionReason,
            AssignedToUserId = assignedToUserId,
            PerformedByUserId = userId,
            PerformedByServiceAccountId = serviceAccountId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static InvalidWorkflowTransitionException InvalidTransition(WorkflowStatus from, string action) =>
        new($"Cannot {action} a document whose workflow status is {WorkflowStatusName(from)}.");

    private async Task<WorkflowResource> BuildResourceAsync(Guid documentId, DocumentVersion version, EffectiveRights rights, CancellationToken cancellationToken)
    {
        var state = await LoadStateAsync(version.Id, cancellationToken);
        var status = state?.Status ?? WorkflowStatus.Draft;

        var transitions = state is null
            ? new List<WorkflowTransition>()
            : await _dbContext.WorkflowTransitions
                .Where(t => t.WorkflowStateId == state.Id)
                .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
                .ToListAsync(cancellationToken);

        // Resolve display names for assignee + performers in one pass.
        var userIds = transitions.SelectMany(t => new[] { t.AssignedToUserId, t.PerformedByUserId }).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        if (state?.AssignedToUserId is { } aid) userIds.Add(aid);
        var serviceAccountIds = transitions.Where(t => t.PerformedByServiceAccountId is not null).Select(t => t.PerformedByServiceAccountId!.Value).ToHashSet();

        var userNames = await _dbContext.Users.Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, cancellationToken);
        var serviceAccountNames = await _dbContext.ServiceAccounts.Where(s => serviceAccountIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        string? Name(Guid? userId, Guid? serviceAccountId) =>
            userId is { } u ? userNames.GetValueOrDefault(u)
            : serviceAccountId is { } s ? serviceAccountNames.GetValueOrDefault(s)
            : null;

        var basePath = $"/api/documents/{documentId}/versions/{version.Id}/workflow";
        var links = new List<Link> { new("self", basePath, "GET") };

        // Valid-transition action links, present only when the transition is valid for the current state AND
        // permitted for the caller.
        var confirmed = version.Status == DocumentVersionStatus.Confirmed;
        var isReviewer = _currentUserAccessor.UserId is { } me && state?.AssignedToUserId == me;
        if (confirmed && status is WorkflowStatus.Draft or WorkflowStatus.Rejected && rights.CanEditContent)
        {
            links.Add(new Link("submit", $"{basePath}/submit", "POST"));
        }

        if (status == WorkflowStatus.InReview && isReviewer)
        {
            links.Add(new Link("approve", $"{basePath}/approve", "POST"));
            links.Add(new Link("reject", $"{basePath}/reject", "POST"));
        }

        // Reassign (delegate/re-route the review) — the assigned reviewer or any CanEditContent holder.
        if (status == WorkflowStatus.InReview && (isReviewer || rights.CanEditContent))
        {
            links.Add(new Link("reassign", $"{basePath}/reassign", "POST"));
        }

        if (status == WorkflowStatus.Approved && rights.CanEditContent)
        {
            links.Add(new Link("release", $"{basePath}/release", "POST"));
        }

        return new WorkflowResource
        {
            Status = (int)status,
            StatusName = WorkflowStatusName(status),
            AssignedToUserId = state?.AssignedToUserId,
            AssignedToName = state?.AssignedToUserId is { } a ? userNames.GetValueOrDefault(a) : null,
            DueAt = state?.DueAt,
            IsOverdue = state is { Status: WorkflowStatus.InReview, DueAt: { } due } && DateTimeOffset.UtcNow > due,
            History = transitions.Select(t => new WorkflowTransitionResource
            {
                FromStatus = (int)t.FromStatus,
                FromStatusName = WorkflowStatusName(t.FromStatus),
                ToStatus = (int)t.ToStatus,
                ToStatusName = WorkflowStatusName(t.ToStatus),
                RejectionReason = t.RejectionReason,
                AssignedToName = t.AssignedToUserId is { } ta ? userNames.GetValueOrDefault(ta) : null,
                PerformedByName = Name(t.PerformedByUserId, t.PerformedByServiceAccountId),
                CreatedAt = t.CreatedAt,
            }).ToList(),
            Links = links,
        };
    }

    private static string WorkflowStatusName(WorkflowStatus status) => status switch
    {
        WorkflowStatus.Draft => "Draft",
        WorkflowStatus.InReview => "In Review",
        WorkflowStatus.Approved => "Approved",
        WorkflowStatus.Rejected => "Rejected",
        WorkflowStatus.Released => "Released",
        _ => status.ToString(),
    };

    private async Task<EffectiveRights> GetCallerRightsAsync(Guid documentId, CancellationToken cancellationToken)
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsForServiceAccountAsync(serviceAccountId, documentId, cancellationToken);
        }

        if (_currentUserAccessor.UserId is { } userId)
        {
            return await _effectiveRightsCalculator.GetEffectiveRightsAsync(userId, documentId, cancellationToken);
        }

        return new EffectiveRights(false, false, false, false, false, false, false, false, false);
    }

    private (Guid? UserId, Guid? ServiceAccountId) GetCallerIdentity()
    {
        if (_currentServiceAccountAccessor.ServiceAccountId is { } serviceAccountId)
        {
            return (null, serviceAccountId);
        }

        return (_currentUserAccessor.UserId, null);
    }

    // ---- DTOs (mutable classes for XML serialization, per ADR "JSON/XML content negotiation") -----------

    public class SubmitRequest
    {
        public Guid ReviewerId { get; set; }
    }

    public class RejectRequest
    {
        public string? Reason { get; set; }
    }

    public class ReassignRequest
    {
        public Guid ReviewerId { get; set; }
    }

    public class WorkflowResource : HypermediaResource
    {
        public int Status { get; set; }
        public string StatusName { get; set; } = "";
        public Guid? AssignedToUserId { get; set; }
        public string? AssignedToName { get; set; }
        public DateTimeOffset? DueAt { get; set; }
        public bool IsOverdue { get; set; }
        public List<WorkflowTransitionResource> History { get; set; } = [];
    }

    public class WorkflowTransitionResource
    {
        public int FromStatus { get; set; }
        public string FromStatusName { get; set; } = "";
        public int ToStatus { get; set; }
        public string ToStatusName { get; set; } = "";
        public string? RejectionReason { get; set; }
        public string? AssignedToName { get; set; }
        public string? PerformedByName { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }
}

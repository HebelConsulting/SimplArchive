using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// Backs the workflow window, opened on demand from the Repositories ribbon / row context menu (ADR
// "Workflow start on demand", superseding the inline detail-pane panel of ADR 0298/0299). Resolves the
// document's latest confirmed version workflow, shows the current status + whichever transitions are valid +
// the history, and drives Submit/Approve/Reject/Release. `Changed` is set once any action succeeds, so the
// caller can refresh the Tasks badge.
public sealed partial class WorkflowWindowViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;
    private readonly Guid _documentId;
    private Dictionary<string, string> _links = new();

    public WorkflowWindowViewModel(SimplArchiveApiClient api, Guid documentId)
    {
        _api = api;
        _documentId = documentId;
    }

    public bool Changed { get; private set; }

    [ObservableProperty] private bool _hasWorkflow;
    [ObservableProperty] private string _statusName = "";
    [ObservableProperty] private string? _assignedTo;
    [ObservableProperty] private bool _canSubmit;
    [ObservableProperty] private bool _canReview;   // approve/reject (the assigned reviewer)
    [ObservableProperty] private bool _canRelease;
    [ObservableProperty] private bool _canReassign;  // delegate/re-route (reviewer or editor)
    [ObservableProperty] private bool _hasHistory;
    [ObservableProperty] private SimplArchiveApiClient.UserOptionInfo? _selectedReviewer;
    [ObservableProperty] private string _rejectReason = "";
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<WorkflowHistoryItemViewModel> History { get; } = [];
    public ObservableCollection<SimplArchiveApiClient.UserOptionInfo> Reviewers { get; } = [];

    public async Task LoadAsync()
    {
        HasWorkflow = false;
        History.Clear();
        CanSubmit = CanReview = CanRelease = CanReassign = false;
        RejectReason = "";
        SelectedReviewer = null;

        WorkflowClient.WorkflowInfo? wf;
        try
        {
            wf = await _api.Documents.GetWorkflowAsync(_documentId);
        }
        catch (Exception e)
        {
            Status = string.Format(Strings.Get("StErrLoad"), e.Message);
            return;
        }

        if (wf is null)
        {
            Status = Strings.Get("WfNoVersion");
            return;
        }

        HasWorkflow = true;
        StatusName = wf.StatusName;
        AssignedTo = wf.AssignedToName is { } a ? $"reviewer: {a}" : null;
        _links = new Dictionary<string, string>(wf.Links);
        CanSubmit = wf.Links.ContainsKey("submit");
        CanReview = wf.Links.ContainsKey("approve"); // approve + reject travel together (assigned reviewer)
        CanRelease = wf.Links.ContainsKey("release");
        CanReassign = wf.Links.ContainsKey("reassign");

        foreach (var h in wf.History)
        {
            var detail = $"by {h.PerformedByName}"
                + (h.AssignedToName is { } to ? $" → {to}" : "")
                + (h.RejectionReason is { } r ? $" · {r}" : "");
            History.Add(new WorkflowHistoryItemViewModel { ToStatusName = h.ToStatusName, Detail = detail });
        }

        HasHistory = History.Count > 0;

        // Populate the reviewer picker when a submit or reassign is offered — any editor may read this catalog
        // (ADR "Workflow assignable-reviewers endpoint"), no CanManageUsers needed.
        if ((CanSubmit || CanReassign) && Reviewers.Count == 0)
        {
            foreach (var u in await _api.Documents.GetAssignableReviewersAsync(_documentId))
            {
                Reviewers.Add(u);
            }
        }
    }

    [RelayCommand]
    private Task Submit() => PostAsync("submit", new { reviewerId = SelectedReviewer?.Id });

    [RelayCommand]
    private Task Approve() => PostAsync("approve", null);

    [RelayCommand]
    private Task Reject() => PostAsync("reject", new { reason = RejectReason });

    [RelayCommand]
    private Task Release() => PostAsync("release", null);

    [RelayCommand]
    private Task Reassign() => PostAsync("reassign", new { reviewerId = SelectedReviewer?.Id });

    private async Task PostAsync(string rel, object? body)
    {
        if (!_links.TryGetValue(rel, out var href))
        {
            return;
        }

        if (rel is "submit" or "reassign" && SelectedReviewer is null)
        {
            Status = Strings.Get("StPickReviewer");
            return;
        }

        if (rel == "reject" && string.IsNullOrWhiteSpace(RejectReason))
        {
            Status = Strings.Get("StRejectReasonReq");
            return;
        }

        Busy = true;
        try
        {
            await _api.Workflow.PostWorkflowActionAsync(href, body);
            Changed = true;
            await LoadAsync();
            Status = string.Format(Strings.Get("StWorkflowStatus"), StatusName);
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}

// A row in the Tasks tab — a pending review task assigned to the current user (ADR "Workflow / document
// state model", 0009).
public sealed class TaskItemViewModel
{
    public required Guid DocumentId { get; init; }
    public Guid? ParentId { get; init; }

    /// <summary>The row's advertised addresses (`document`, `parent`, `workflow`) — opening follows these (#443).</summary>
    public IReadOnlyDictionary<string, string>? Links { get; init; }
    public required string DocumentName { get; init; }
    public int? VersionNumber { get; init; }
    public DateTimeOffset AssignedAt { get; init; }

    public DateTimeOffset? DueAt { get; init; }

    public string VersionText => VersionNumber is { } n ? $"v{n}" : "—";
    public string AssignedText => AssignedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string DueText => DueAt is { } due ? due.LocalDateTime.ToString("yyyy-MM-dd") : "—";
    public bool IsOverdue => DueAt is { } d && DateTimeOffset.Now > d;
}

// A row in the workflow history (a single transition, pre-formatted for display).
public sealed class WorkflowHistoryItemViewModel
{
    public required string ToStatusName { get; init; }
    public required string Detail { get; init; }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The Tasks tab's sort + filter state (#550): every data column sorts on a header click (Status stays plain —
// a task IS a review, the chip is invariant), and a visible filter row narrows by document/version text and
// an overdue-only toggle (ADR 0550: the affordance is on screen, not behind a menu). All of it is client-side
// over the loaded page — the tasks API's cursor order stays untouched, and at current volumes the loaded page
// IS the list (the issue notes a server-side ?sort= becomes an API question only if that stops being true).
public partial class MainWindowViewModel
{
    /// <summary>What the Tasks list actually shows: <see cref="Tasks"/> filtered + sorted.</summary>
    public ObservableCollection<TaskItemViewModel> VisibleTasks { get; } = [];

    [ObservableProperty] private string _taskFilterDocument = string.Empty;
    [ObservableProperty] private string _taskFilterVersion = string.Empty;
    [ObservableProperty] private bool _taskFilterOverdueOnly;

    // Default: due-first (overdue on top, no-due-date last) — what a task list is for. A second click on the
    // same header flips the direction.
    [ObservableProperty] private string _taskSortColumn = "due";
    [ObservableProperty] private bool _taskSortAscending = true;

    partial void OnTaskFilterDocumentChanged(string value) => RebuildVisibleTasks();
    partial void OnTaskFilterVersionChanged(string value) => RebuildVisibleTasks();
    partial void OnTaskFilterOverdueOnlyChanged(bool value) => RebuildVisibleTasks();

    [RelayCommand]
    private void SortTasks(string column)
    {
        if (TaskSortColumn == column)
        {
            TaskSortAscending = !TaskSortAscending;
        }
        else
        {
            TaskSortColumn = column;
            TaskSortAscending = true;
        }

        RebuildVisibleTasks();
        OnPropertyChanged(nameof(TaskHeaderDocument));
        OnPropertyChanged(nameof(TaskHeaderVersion));
        OnPropertyChanged(nameof(TaskHeaderAssigned));
        OnPropertyChanged(nameof(TaskHeaderDue));
    }

    // Header captions carry the sort direction, so the state is visible where it was set.
    public string TaskHeaderDocument => TaskHeader(Strings.Get("ColDocument"), "document");
    public string TaskHeaderVersion => TaskHeader(Strings.Get("VerColVersion"), "version");
    public string TaskHeaderAssigned => TaskHeader(Strings.Get("ColAssigned"), "assigned");
    public string TaskHeaderDue => TaskHeader(Strings.Get("ColDue"), "due");

    private string TaskHeader(string label, string column) =>
        TaskSortColumn == column ? $"{label} {(TaskSortAscending ? "▲" : "▼")}" : label;

    /// <summary>Recomputes <see cref="VisibleTasks"/>. Called after every load and every sort/filter change.</summary>
    public void RebuildVisibleTasks()
    {
        IEnumerable<TaskItemViewModel> rows = Tasks;
        if (!string.IsNullOrWhiteSpace(TaskFilterDocument))
        {
            rows = rows.Where(t => t.DocumentName.Contains(TaskFilterDocument.Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(TaskFilterVersion))
        {
            rows = rows.Where(t => t.VersionText.Contains(TaskFilterVersion.Trim(), StringComparison.CurrentCultureIgnoreCase));
        }

        if (TaskFilterOverdueOnly)
        {
            rows = rows.Where(t => t.IsOverdue);
        }

        rows = TaskSortColumn switch
        {
            "document" => rows.OrderBy(t => t.DocumentName, StringComparer.CurrentCultureIgnoreCase),
            "version" => rows.OrderBy(t => t.VersionNumber ?? int.MinValue),
            "assigned" => rows.OrderBy(t => t.AssignedAt),
            // Due: null (no due date) sorts LAST in the ascending view — a task without a deadline is never
            // the most urgent one.
            _ => rows.OrderBy(t => t.DueAt ?? DateTimeOffset.MaxValue).ThenBy(t => t.AssignedAt),
        };

        if (!TaskSortAscending)
        {
            rows = rows.Reverse();
        }

        VisibleTasks.Clear();
        foreach (var row in rows)
        {
            VisibleTasks.Add(row);
        }
    }

    // ---- The list itself: loading it, its badge, and opening a task -------------------------------------
    //
    // These were in TWO PLACES, separated by the entire notifications block: the collection and badge sat under
    // a "Workflow + tasks" heading, while ReloadTasksAsync, LoadTasksAsync and OpenTask were a hundred lines
    // away at the tail of the notifications section. Neither half was wrong about what it was -- they had
    // drifted apart, and the heading between them hid it (#941). They join the filtering that was already here,
    // which is what actually reads Tasks.
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasTasks))] private int _taskCount;
    public bool HasTasks => TaskCount > 0;

    [RelayCommand]
    private Task RefreshTasks() => LoadTasksAsync();

    // Builds the view-model for the on-demand workflow window (ADR "Workflow start on demand"). Opened +
    // loaded by the code-behind (which owns the Window); null when there's no api client or no document
    // selected. The window drives Submit/Approve/Reject/Release against the selected document's latest
    // confirmed version.
    public WorkflowWindowViewModel? CreateWorkflowViewModel()
    {
        if (_api is null || SelectedItem is not { IsFolder: false, IsArchiveEntry: false, IsArchiveBack: false } item)
        {
            return null;
        }

        return new WorkflowWindowViewModel(_api, item.Href("versions"), item.DocumentSelfHref);
    }

    // Reloads the Tasks-tab list + badge — called after the workflow window closes, since a submit/approve/etc.
    // changes the caller's (or the reviewer's) pending-task set.
    public Task ReloadTasksAsync() => LoadTasksAsync();

    private async Task LoadTasksAsync()
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            Tasks.Clear();
            foreach (var t in await _api.Workflow.GetTasksAsync())
            {
                Tasks.Add(new TaskItemViewModel
                {
                    DocumentId = t.DocumentId,
                    ParentId = t.ParentId,
                    Links = t.Links,
                    DocumentName = t.DocumentName,
                    VersionNumber = t.VersionNumber,
                    AssignedAt = t.AssignedAt,
                    DueAt = t.DueAt,
                });
            }

            TaskCount = Tasks.Count;
            RebuildVisibleTasks();
        }
        catch (Exception)
        {
            TaskCount = 0;
        }
    }

    [RelayCommand]
    private async Task OpenTask(TaskItemViewModel? task)
    {
        if (task is null)
        {
            return;
        }

        // Follow the row's `parent` and select the document there; a root document opens itself (#443).
        if ((task.Links?.GetValueOrDefault("parent") ?? task.Links?.GetValueOrDefault("document")) is { } href)
        {
            SelectedTab = 0; // Repositories
            await OpenFolderAsync(href, task.Links?.ContainsKey("parent") == true ? task.DocumentId : null);
        }
    }
}

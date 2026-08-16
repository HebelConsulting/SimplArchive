using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Tasks tab's sort + filter logic (#550), at the view-model level — pure state, no server, no display.
// Default order is due-first (overdue on top, no due date last); headers toggle; the visible filter row
// narrows by document/version text and the overdue-only switch.
public class DesktopTasksSortFilterTests
{
    private static MainWindowViewModel VmWithTasks()
    {
        var vm = new MainWindowViewModel();
        var now = DateTimeOffset.Now;
        vm.Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Beta.pdf", VersionNumber = 2, AssignedAt = now.AddDays(-1), DueAt = now.AddDays(5) });
        vm.Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Alpha.pdf", VersionNumber = 1, AssignedAt = now.AddDays(-3), DueAt = now.AddDays(-2) });
        vm.Tasks.Add(new TaskItemViewModel { DocumentId = Guid.NewGuid(), DocumentName = "Gamma.pdf", VersionNumber = 3, AssignedAt = now.AddDays(-2), DueAt = null });
        vm.RebuildVisibleTasks();
        return vm;
    }

    [Fact]
    public void Default_order_is_due_first_with_no_due_date_last()
    {
        var vm = VmWithTasks();
        Assert.Equal(["Alpha.pdf", "Beta.pdf", "Gamma.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));
    }

    [Fact]
    public void Clicking_a_header_sorts_and_clicking_it_again_flips()
    {
        var vm = VmWithTasks();

        vm.SortTasksCommand.Execute("document");
        Assert.Equal(["Alpha.pdf", "Beta.pdf", "Gamma.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));

        vm.SortTasksCommand.Execute("document");
        Assert.Equal(["Gamma.pdf", "Beta.pdf", "Alpha.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));

        // The caption carries the state — the arrow lives where the sort was set.
        Assert.Contains("▼", vm.TaskHeaderDocument);
        Assert.DoesNotContain("▲", vm.TaskHeaderDue);
    }

    [Fact]
    public void The_filter_row_narrows_and_clearing_restores()
    {
        var vm = VmWithTasks();

        vm.TaskFilterDocument = "alp";
        Assert.Equal(["Alpha.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));

        vm.TaskFilterDocument = string.Empty;
        Assert.Equal(3, vm.VisibleTasks.Count);

        vm.TaskFilterVersion = "v3";
        Assert.Equal(["Gamma.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));
        vm.TaskFilterVersion = string.Empty;

        // Overdue-only: exactly the row whose due date is in the past.
        vm.TaskFilterOverdueOnly = true;
        Assert.Equal(["Alpha.pdf"], vm.VisibleTasks.Select(t => t.DocumentName));
    }
}

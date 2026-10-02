using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The open-task count, shown OUTSIDE the window (#502).
//
// The in-app badge can only tell you work is waiting while the window is in front of you, which is the one
// moment you do not need telling. These pin the view-model half — that the count reaches the platform surface
// at all, and that it reaches it as the same number the Tasks tab shows.
//
// What they deliberately do NOT claim is that a badge APPEARS: that needs a Dock or a taskbar, and a headless
// run has neither. The platform implementations are verified by compiling for both targets and by their own
// Trace lines; a visible badge needs a machine with a desktop session.
public class TaskbarBadgeTests
{
    private sealed class RecordingBadge : ITaskbarBadge
    {
        public List<int> Shown { get; } = [];

        public void Show(int count) => Shown.Add(count);
    }

    [Fact]
    public void The_count_reaches_the_platform_surface_when_it_changes()
    {
        var badge = new RecordingBadge();
        var vm = new MainWindowViewModel { TaskbarBadge = badge };

        vm.TaskCount = 3;

        Assert.Equal([3], badge.Shown);
    }

    [Fact]
    public void Reaching_zero_is_pushed_too()
    {
        // The clear is the half that would be forgotten, and the one whose absence is worst: a badge left
        // showing 2 after the last task is done tells somebody there is work waiting when there is none, and
        // they can only find out by opening the window the badge exists to save them opening.
        var badge = new RecordingBadge();
        var vm = new MainWindowViewModel { TaskbarBadge = badge };

        vm.TaskCount = 2;
        vm.TaskCount = 0;

        Assert.Equal([2, 0], badge.Shown);
    }

    [Fact]
    public void It_is_the_same_number_the_tasks_tab_shows()
    {
        // Not a second calculation. Nobody can compare the two — the window is behind something, which is the
        // whole point — so a divergence would never be noticed.
        var badge = new RecordingBadge();
        var vm = new MainWindowViewModel { TaskbarBadge = badge };

        vm.TaskCount = 7;

        Assert.Equal(vm.TaskCount, badge.Shown.Single());
        Assert.True(vm.HasTasks);
    }

    [Fact]
    public void Every_platform_gets_a_badge_rather_than_a_null()
    {
        // The factory answers for every platform, including the two with no implementation — so a caller never
        // has to null-check, and "unsupported" is a no-op that SAYS so rather than an absent object.
        Assert.NotNull(TaskbarBadge.ForThisPlatform());
    }
}

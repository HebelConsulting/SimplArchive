using Avalonia.Controls;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Reset pane layout restores the workbench's default split (#895).
//
// This existed only as the manual `--reset-layout-test` hook, and that is exactly how it broke: #413 changed
// the index pane to expand to Auto rather than to a remembered height, the hook went on asserting the old
// 1.5* default, and it reported FAILED for months with nothing to notice — a headless hook is run when someone
// remembers to run it, which is never. So the behaviour is asserted HERE, where CI runs it, and the hook now
// agrees with this rather than the other way round.
public class DesktopResetLayoutTests
{
    [Fact]
    public void Reset_layout_re_expands_every_pane_and_restores_the_default_split()
    {
        var vm = new MainWindowViewModel();
        vm.ToggleTreeCommand.Execute(null);
        vm.ToggleListCommand.Execute(null);
        vm.ToggleIndexCommand.Execute(null);
        vm.ToggleChatCommand.Execute(null);

        Assert.True(vm.TreeCollapsed && vm.ListCollapsed && vm.IndexCollapsed && vm.ChatCollapsed,
            "the panes must actually be collapsed first, or the reset below proves nothing.");

        vm.ResetLayoutCommand.Execute(null);

        Assert.False(vm.TreeCollapsed);
        Assert.False(vm.ListCollapsed);
        Assert.False(vm.IndexCollapsed);
        Assert.False(vm.ChatCollapsed);

        Assert.Equal(new GridLength(1.4, GridUnitType.Star), vm.TreeWidth);
        Assert.Equal(new GridLength(2, GridUnitType.Star), vm.ListWidth);
        Assert.Equal(new GridLength(2, GridUnitType.Star), vm.ChatWidth);

        // AUTO, not a proportion: the detail pane fits its content (ADR 0550), and #413 removed its remembered
        // height so that one drag — a PEEK — cannot survive a collapse/expand cycle, or reach the settings
        // file. Asserting a star value here would be re-introducing the bug this test exists to hold down.
        Assert.True(vm.IndexHeight.IsAuto, $"the index pane must reset to Auto, not {vm.IndexHeight}.");
    }
}

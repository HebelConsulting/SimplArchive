using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// ⌘/Ctrl+O opens the selected document (#482, ADR "One shortcut for opening a document").
//
// The assertions that matter here are the NEGATIVE ones. Binding the chord where Open means "open natively" is
// the easy half; what needs guarding is that it stays off the tabs whose "Open" means something else — Search
// and Tasks REVEAL the document in Repositories — because that is the change a later reader would make thinking
// they were completing the feature, and one chord with two meanings is a chord nobody trusts.
public class DesktopOpenShortcutTests
{
    [Fact]
    public async Task The_chord_does_nothing_on_a_search_result()
    {
        var vm = new MainWindowViewModel { SelectedTab = 3 }; // Search
        vm.Search.SelectedSearchResult = new SearchResultViewModel
        {
            Id = Guid.NewGuid(),
            Name = "a result",
            IsFolder = false,
            ParentId = null,
            Path = string.Empty,
        };

        await vm.OpenSelectedCommand.ExecuteAsync(null);

        // Revealing a result switches to the Repositories tab, so a tab that stayed put is the proof that the
        // chord did not act. Asserting "nothing happened" any other way would pass for the wrong reason.
        Assert.Equal(3, vm.SelectedTab);
    }

    [Fact]
    public async Task The_chord_is_a_no_op_on_every_tab_when_nothing_is_selected()
    {
        var vm = new MainWindowViewModel();

        // Pressed on an empty folder, an empty intray, or a tab with no notion of a selected document at all —
        // the handler marks the key handled either way, so this must not throw.
        for (var tab = 0; tab <= 3; tab++)
        {
            vm.SelectedTab = tab;
            await vm.OpenSelectedCommand.ExecuteAsync(null);
        }

        Assert.Equal(3, vm.SelectedTab); // still where we left it — nothing navigated
    }

    [Fact]
    public void The_chord_is_advertised_on_the_affordances_that_are_not_menu_entries()
    {
        // A MenuItem carries an InputGesture and renders it itself; a plain button cannot, so the ribbon's and
        // the Intray row's Open put the chord in the tooltip. A shortcut nobody can discover is one nobody uses.
        var chord = OperatingSystem.IsMacOS() ? "Cmd+O" : "Ctrl+O";

        Assert.EndsWith(chord, MainWindowViewModel.OpenTip);
        Assert.EndsWith(chord, MainWindowViewModel.RibbonOpenTip);
    }
}

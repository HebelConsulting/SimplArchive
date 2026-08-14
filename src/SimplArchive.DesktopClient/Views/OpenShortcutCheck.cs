using Avalonia;
using Avalonia.Headless;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--shortcut-test</c>: the Open chord (#482, ADR "One shortcut for opening a
/// document").
/// </summary>
/// <remarks>
/// <para>
/// Prints the chord as the user will see it in the menu — which is the whole point of advertising it — and
/// checks that the command dispatches ONLY on the tabs whose Open means "open natively".
/// </para>
/// <para>
/// <b>The negative half is the one that matters:</b> a Search result is "opened" by revealing it in
/// Repositories, which would switch the tab, so a tab that stays put proves the chord did not quietly acquire a
/// second meaning.
/// </para>
/// <para>
/// Moved out of <c>Program.cs</c> when the next verification hook arrived (#503): that file is on the
/// 1000-line standing-debt list (issue #466) and may only get smaller, so a hook that needs a home takes one
/// with it rather than paying for its dispatch line with a raised ceiling.
/// </para>
/// </remarks>
public static class OpenShortcutCheck
{
    /// <summary>Runs the check and prints <c>OK</c> or <c>FAILED</c>. Returns true when everything held.</summary>
    public static bool Run()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var vm = new MainWindowViewModel();
        var chord = Services.Shortcuts.Open.ToString();

        // Every tab, nothing selected: a no-op, never a crash.
        var survivedEmpty = true;
        for (var tab = 0; tab <= 3; tab++)
        {
            vm.SelectedTab = tab;
            try { vm.OpenSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult(); }
            catch (Exception ex) { survivedEmpty = false; Console.WriteLine($"tab {tab} threw: {ex.Message}"); }
        }

        // Search: a selected result the chord must NOT act on. Revealing it would set SelectedTab to 0.
        vm.SelectedTab = 3;
        vm.SelectedSearchResult = new SearchResultViewModel
        {
            Id = Guid.NewGuid(),
            Name = "irrelevant",
            IsFolder = false,
            ParentId = null,
            Path = string.Empty,
        };
        vm.OpenSelectedCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var searchUntouched = vm.SelectedTab == 3;

        var tipCarriesChord = MainWindowViewModel.OpenTip.Contains(chord) && MainWindowViewModel.RibbonOpenTip.Contains(chord);
        var passed = survivedEmpty && searchUntouched && tipCarriesChord;

        Console.WriteLine($"chord: {chord} | ribbon tooltip: {MainWindowViewModel.RibbonOpenTip}");
        Console.WriteLine($"survivedEmpty={survivedEmpty} searchUntouched={searchUntouched} tipCarriesChord={tipCarriesChord}");
        Console.WriteLine(passed ? "OK" : "FAILED");
        return passed;
    }
}

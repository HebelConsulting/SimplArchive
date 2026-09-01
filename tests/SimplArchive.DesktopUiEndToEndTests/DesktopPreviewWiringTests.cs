using System.Reflection;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// Every preview surface in the window is reachable through MainWindowViewModel.PreviewSurfaces.
//
// WHY THIS EXISTS. The API client was handed to previews in one place and taken away in another, and the two
// lists drifted: the Search tab's preview was in NEITHER, so it silently showed nothing — PreviewViewModel's
// RenderAsync begins `if (Api is null) return;`, so a preview with no client is not an error, it is a blank
// pane. Check-out's and Recycle bin's kept their client after sign-out, which is the same drift facing the
// other way.
//
// The fix was one list read by both paths. This test is what keeps the list COMPLETE: a new tab that owns a
// preview is found by reflection here, and fails until it is added. Without it the list is just a third place
// to forget — which is how the Search tab got missed when its view moved to its own DataContext (9ca3b3ef).
public class DesktopPreviewWiringTests
{
    [Fact]
    public void Every_preview_in_the_window_is_listed_in_PreviewSurfaces()
    {
        var vm = new MainWindowViewModel();
        var listed = vm.PreviewSurfaces.ToList();

        // Every PreviewViewModel hanging off the shell, or off anything the shell exposes (the tab view-models).
        var found = new List<(string Path, PreviewViewModel Preview)>();
        foreach (var owner in Owners(vm))
        {
            foreach (var property in owner.Value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.PropertyType == typeof(PreviewViewModel)
                    && property.GetValue(owner.Value) is PreviewViewModel preview)
                {
                    found.Add(($"{owner.Key}{property.Name}", preview));
                }
            }
        }

        var missing = found
            .Where(f => !listed.Any(l => ReferenceEquals(l, f.Preview)))
            .Select(f => f.Path)
            .ToList();

        Assert.True(missing.Count == 0,
            "These preview surfaces are not in MainWindowViewModel.PreviewSurfaces, so they will neither be given "
            + "an API client on sign-in nor have it taken away on sign-out — and a preview with no client renders "
            + $"BLANK rather than failing:\n  {string.Join("\n  ", missing)}");

        Assert.NotEmpty(listed);
    }

    // The shell itself, plus each view-model it exposes (that is where a tab's own preview lives).
    private static Dictionary<string, object> Owners(MainWindowViewModel vm)
    {
        var owners = new Dictionary<string, object> { [""] = vm };
        foreach (var property in typeof(MainWindowViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.PropertyType.Name.EndsWith("TabViewModel", StringComparison.Ordinal)
                && property.GetValue(vm) is { } tab)
            {
                owners[property.Name + "."] = tab;
            }
        }

        return owners;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

/// <summary>
/// The headless check behind <c>--column-drag-test</c>: the contents-list header actually resizes a column when
/// its edge is DRAGGED (issue #786, ADR "Desktop list-pane resizable columns").
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> <c>--columns-test</c> drives <see cref="MainWindowViewModel.ResizeColumn"/> directly
/// and passes, and the feature was still broken end-to-end: nothing ever exercised the Thumb, so the arithmetic
/// was proven and the affordance that reaches it was not. The comment in <c>Program.cs</c> said the drag
/// "needs a real desktop" — that assumption is what left it untested, and it is wrong in the same way the one
/// about context menus was (CLAUDE.md: a context menu DOES render headlessly). <c>Avalonia.Headless</c>
/// simulates pointer input, so a drag is testable without a display.
/// </para>
/// <para>
/// The check is deliberately in two halves, because they fail for different reasons and only one of them was
/// ever the bug: <b>hit-testing</b> asks whether the pointer lands on the Thumb at all (a control with no
/// rendered geometry receives nothing, whatever its handlers say), and <b>the drag</b> asks whether landing on
/// it changes a width. A fix that satisfies the second without the first would mean the test is driving the
/// handler rather than the user's gesture.
/// </para>
/// </remarks>
public static class ColumnDragCheck
{
    /// <summary>Runs the check and prints <c>OK</c> or <c>FAILED</c>. Returns true when everything held.</summary>
    public static bool Run()
    {
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        var vm = new MainWindowViewModel();
        // The whole workbench is `IsVisible="{Binding IsLoggedIn}"` (MainWindow.axaml), so a fresh view-model
        // renders a window with ~40 visuals and NO contents list at all. Without this line the check inspects
        // an empty window and reports the Thumb missing — which looks exactly like the bug it is hunting.
        vm.IsLoggedIn = true;
        // Give the list pane more room than the columns need. At the default split the pane is ~299px while the
        // header is 852px wide, so four of the six grips are scrolled out of the viewport — and a CLIPPED
        // control is legitimately not hit-testable, which would make this check report the bug it is hunting
        // for the wrong reason. (That the columns overflow a default pane at all is the other half of #786.)
        vm.TreeWidth = new GridLength(1, GridUnitType.Star);
        vm.ListWidth = new GridLength(20, GridUnitType.Star);
        vm.ChatWidth = new GridLength(1, GridUnitType.Star);
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        // Compose a frame before probing. Show()+RunJobs() lays out but does not RENDER, and Avalonia hit-tests
        // the composed tree — without this the probe below reports an ancestor for EVERY control, the header
        // Button included, which reads as "the Thumb is not hit-testable" when nothing is.
        using (var _ = window.CaptureRenderedFrame()) { }
        Dispatcher.UIThread.RunJobs();

        // The Thumb for column 1 (Type). Column 0's edge is the interesting one for the FLEX rule, but Type is
        // the honest subject for "does a drag move a width": its own stored width is what changes either way.
        var thumb = window.GetVisualDescendants().OfType<Thumb>()
            .FirstOrDefault(t => t.Tag?.ToString() == "1");
        if (thumb is null)
        {
            var allThumbs = window.GetVisualDescendants().OfType<Thumb>().ToList();
            var list = window.GetVisualDescendants().OfType<ListBox>()
                .FirstOrDefault(l => l.Name == "ContentsList");
            Console.WriteLine($"DIAG window.Bounds={window.Bounds} ClientSize={window.ClientSize}");
            Console.WriteLine($"DIAG thumbs={allThumbs.Count} tags=[{string.Join(",", allThumbs.Select(t => t.Tag?.ToString() ?? "-"))}]");
            Console.WriteLine($"DIAG ContentsList={(list is null ? "absent" : $"present bounds={list.Bounds}")}");
            Console.WriteLine($"DIAG visuals={window.GetVisualDescendants().Count()} scrollviewers={window.GetVisualDescendants().OfType<ScrollViewer>().Count()}");
            Console.WriteLine("no Thumb with Tag=1 in the visual tree");
            Console.WriteLine("FAILED");
            return false;
        }

        var bounds = thumb.Bounds;
        var origin = thumb.TranslatePoint(new Point(bounds.Width / 2, bounds.Height / 2), window);
        if (origin is not { } centre)
        {
            Console.WriteLine("the Thumb is not in the window's coordinate space (never arranged)");
            Console.WriteLine("FAILED");
            return false;
        }

        // Half one: does the pointer reach it? `InputHitTest` answers with the control the platform would
        // deliver a press to. A templateless control renders no geometry and is not it, however its handlers
        // are wired — which is exactly the shape this bug had.
        // The control probe earns its place: if a point squarely inside the header BUTTON also reports an
        // ancestor, the hit test is being asked wrongly and says nothing about the Thumb. Both readings were
        // needed to tell "the grip is dead" from "the grip is scrolled out of the viewport" (#786).
        var buttonProbe = window.InputHitTest(centre - new Point(20, 0));

        var hit = window.InputHitTest(centre);
        var hitsThumb = hit is Thumb || (hit as Visual)?.GetVisualAncestors().OfType<Thumb>().Any() == true;

        // Half two: the gesture itself.
        // The FILL half: the scrollable region must be the pane's width, not a fixed block inside it. Measured
        // from the real viewport rather than from the view-model, so the binding is under test too.
        var viewer = window.GetVisualDescendants().OfType<ScrollViewer>()
            .FirstOrDefault(x => x.GetVisualDescendants().Contains(thumb));
        var viewport = viewer?.Viewport.Width ?? 0;
        var fills = viewport > 0 && Math.Abs(vm.ContentsTotalWidth - viewport) < 1.5;

        var before = vm.ColTypeWidth;
        window.MouseDown(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(centre + new Point(40, 0));
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(centre + new Point(40, 0), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        var after = vm.ColTypeWidth;

        var widened = after > before;

        Console.WriteLine($"thumb bounds={bounds} centre={centre} buttonProbe={buttonProbe?.GetType().Name ?? "null"}");
        Console.WriteLine($"hitTest={hit?.GetType().Name ?? "null"} hitsThumb={hitsThumb}");
        Console.WriteLine($"viewport={viewport} total={vm.ContentsTotalWidth} fills={fills}");
        Console.WriteLine($"ColTypeWidth {before} -> {after} widened={widened}");

        // Leave the user's saved layout alone: this check drives the real view-model, which persists on drag
        // completion, so without this a verification run would quietly resize the developer's own columns.
        vm.ResetLayoutCommand.Execute(null);

        var ok = hitsThumb && widened && fills;
        Console.WriteLine(ok ? "OK" : "FAILED");
        return ok;
    }
}

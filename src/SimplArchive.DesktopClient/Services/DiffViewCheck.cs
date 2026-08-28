using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.ViewModels;
using SimplArchive.DesktopClient.Views;

namespace SimplArchive.DesktopClient.Services;

// `--diff-test` (#803, ADR 0712): renders the shared DiffView headlessly and checks what a VM test cannot —
// that the rows actually MATERIALIZE as visuals: four columns per row, the changed words carried as emphasized
// runs with a background, a pure addition showing the gap on the old side. A DiffSegmentText whose inline
// rebuild silently produced nothing would pass every row-level assertion and render an empty panel.
internal static class DiffViewCheck
{
    public static void Run()
    {
        var rows = DiffRowViewModel.Build("the quick brown fox\nsecond line\n", "the quick red fox\nsecond line\nthird line\n");
        var view = new DiffView { RowsSource = rows };
        var window = new Window { Width = 900, Height = 400, Content = view };
        window.Show();
        window.CaptureRenderedFrame(); // forces layout + render of the ItemsControl

        var texts = view.GetVisualDescendants().OfType<DiffSegmentText>().ToList();
        var failures = string.Empty;

        // Row 1 is a changed pair: both sides materialized, each carrying exactly one emphasized run with a
        // background ("brown" old / "red" new), the rest plain.
        var oldSide = texts.FirstOrDefault(t => string.Concat((t.Segments ?? []).Select(s => s.Text)) == "the quick brown fox");
        var newSide = texts.FirstOrDefault(t => string.Concat((t.Segments ?? []).Select(s => s.Text)) == "the quick red fox");
        if (oldSide is null || newSide is null)
        {
            failures += "changed pair did not materialize as DiffSegmentText visuals; ";
        }
        else
        {
            var oldEmph = oldSide.Inlines?.OfType<Run>().Where(r => r.Background is not null).ToList();
            var newEmph = newSide.Inlines?.OfType<Run>().Where(r => r.Background is not null).ToList();
            if (oldEmph is not [{ Text: "brown" }])
            {
                failures += $"old side emphasis was [{string.Join(",", (oldEmph ?? []).Select(r => r.Text))}], expected [brown]; ";
            }

            if (newEmph is not [{ Text: "red" }])
            {
                failures += $"new side emphasis was [{string.Join(",", (newEmph ?? []).Select(r => r.Text))}], expected [red]; ";
            }
        }

        // "third line" is a pure addition: present on the new side, and its row's old cell is an EMPTY
        // DiffSegmentText (the gap), not an absent one — the grid keeps its four columns.
        if (!texts.Any(t => string.Concat((t.Segments ?? []).Select(s => s.Text)) == "third line"))
        {
            failures += "added line did not materialize; ";
        }

        if (texts.Count != rows.Count * 2)
        {
            failures += $"expected {rows.Count * 2} cells ({rows.Count} rows × 2 sides), found {texts.Count}; ";
        }

        Console.WriteLine(failures.Length == 0 ? "DIFF-TEST OK" : $"DIFF-TEST FAIL: {failures}");
        Environment.Exit(failures.Length == 0 ? 0 : 1);
    }
}

namespace SimplArchive.UnitTests;

// Every file picker must be reachable by keyboard (issue #511).
//
// The pattern this bans — `<MudButton HtmlTag="label" for="…">` over a `display:none` `<input type="file">` —
// produces a control with NO keyboard path at all: a label is not focusable, and a hidden input is out of the
// tab order. It also has no `button` role, so assistive technology announces prose rather than something
// actuable. The Inbox's Upload — one of the two ways anything enters the Inbox — was built this way, and two
// more sites (profile photo, import dialog) had copied it, because a label over a hidden input is the obvious
// thing to write.
//
// The fix is a REAL button whose click forwards to the input (`openFilePicker` in filePicker.js) — focus,
// Enter/Space and the role stay native. This guards the SHAPE, in the manner of NoBareApiExceptionTests:
// fixing the three sites that existed is worth little if a fourth reintroduces the pattern, and nothing else
// fails when a control is merely unreachable.
public class KeyboardReachableFilePickersTests
{
    [Fact]
    public void No_client_control_is_a_label_over_a_hidden_input()
    {
        var root = RepoRoot();
        var clientDir = Path.Combine(root, "src", "SimplArchive.Client");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(clientDir, "*.razor", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            var line = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                line++;
                var trimmed = raw.TrimStart();
                if (trimmed.StartsWith("@*", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue; // the comments explaining WHY the pattern is banned would otherwise trip this
                }

                if (trimmed.Contains("HtmlTag=\"label\"", StringComparison.Ordinal))
                {
                    offenders.Add($"  {Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')}:{line}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "A control is rendered as a <label> (HtmlTag=\"label\"), which is unreachable by keyboard and has no "
            + "button role. Use a real MudButton whose OnClick forwards to the hidden input via openFilePicker "
            + "(issue #511):\n" + string.Join("\n", offenders));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

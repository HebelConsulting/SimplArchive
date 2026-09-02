using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// A client must never put the API's Problem Details `detail` in front of a user.
//
// The API's 153 exception classes carry their message as a constructor literal, so `detail` is English no matter
// what Accept-Language says — the request-localization middleware only governs the server-rendered pages. Both
// clients used to display it verbatim, so a German user got German until something went wrong and English exactly
// when it mattered most (issue #424).
//
// The contract is the `errorCode`: language-neutral, stable, and already what the tests assert on (ADR 0543 makes
// codes and rel names the compatibility surface precisely so prose can change freely). The client maps it through
// ApiErrorText and owns the words.
//
// This guards the SHAPE rather than the instances, in the manner of NoBareApiExceptionTests. Fixing the five
// sites that existed is worth little if the sixth reintroduces it — and it would, because reading `detail` is the
// obvious thing to write.
public partial class NoServerDetailInClientsTests
{
    [GeneratedRegex(@"""detail""", RegexOptions.IgnoreCase)]
    private static partial Regex JsonDetailAccess();

    [GeneratedRegex(@"\.Detail\b")]
    private static partial Regex TypedDetailAccess();

    [Fact]
    public void No_client_surfaces_the_servers_problem_detail()
    {
        var root = RepoPaths.Root();
        var offenders = new List<string>();

        foreach (var file in ClientFiles(root))
        {
            var text = File.ReadAllText(file);
            var line = 0;
            foreach (var raw in text.Split('\n'))
            {
                line++;
                if (!JsonDetailAccess().IsMatch(raw) && !TypedDetailAccess().IsMatch(raw))
                {
                    continue;
                }

                // The comments explaining WHY detail is not used would otherwise trip this.
                var trimmed = raw.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("@*", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                offenders.Add($"  {Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')}:{line}  {trimmed.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A client is reading the API's Problem Details `detail`, which is English regardless of the user's "
            + "language. Map the `errorCode` through ApiErrorText instead (issue #424):\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<string> ClientFiles(string root)
    {
        foreach (var project in new[] { "src/SimplArchive.Client", "src/SimplArchive.DesktopClient" })
        {
            var dir = Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}wwwroot{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (Path.GetExtension(file) is ".cs" or ".razor")
                {
                    yield return file;
                }
            }
        }
    }

}

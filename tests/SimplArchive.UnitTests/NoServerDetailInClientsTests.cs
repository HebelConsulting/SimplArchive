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

    // The licensed reads, counted per file exactly (the ApiRoot way): a new read must either flow through
    // these or argue its own license here.
    //
    // Two licenses exist. (1) ADR 0767: a problem carrying a "module" extension has a detail the module's
    // own catalog composed for the REQUEST CULTURE — localized by construction, which is the very property
    // this guard protects. (2) ADR 0769: a proposal ITEM's `Detail` is not an RFC 7807 problem detail at
    // all — it is module-supplied domain text ("Examiner certificate valid to …"), with exactly the
    // standing of a transition button's label: English today, covered by ADR 0737's recorded widening
    // trigger (the per-culture label factory), and rendered beside data the module also names. Renaming
    // the property to dodge this guard was rejected as dishonest — the concern is real and recorded, not
    // absent.
    private static readonly Dictionary<string, int> LicensedModuleDetailReads = new(StringComparer.Ordinal)
    {
        ["src/SimplArchive.Client/Pages/Home.Navigation.razor.cs"] = 2,
        ["src/SimplArchive.DesktopClient/Services/ApiCore.cs"] = 1,
        // ADR 0769's proposal-item Detail (the second license above):
        ["src/SimplArchive.Client/Services/DetailEditor.cs"] = 1,
        ["src/SimplArchive.Client/Components/Panes/IndexDataPane.razor"] = 1,
        ["src/SimplArchive.DesktopClient/ViewModels/MainWindowViewModel.DetailEdit.cs"] = 1,
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.cs"] = 1,
        // ADR 0786's module-action refusal: a module's own sentence, composed by ITS catalog for the request
        // culture, is the one server text a client may show — the same license the proposal Detail has.
        ["src/SimplArchive.DesktopClient/Services/DocumentsClient.ModuleActions.cs"] = 1,
        // ADR 0786's option Detail — the same class as the proposal Detail above, not the API's problem
        // detail: it is the MODULE's sentence about one candidate ("FI(A) · offered and free"), composed by
        // its own catalog for the request culture, and only the module knows what distinguishes its choices.
        ["src/SimplArchive.DesktopClient/ViewModels/ModuleActionPickerViewModel.cs"] = 1,
    };

    [Fact]
    public void No_client_surfaces_the_servers_problem_detail()
    {
        var root = RepoPaths.Root();
        var offenders = new List<string>();
        var licensedSeen = new Dictionary<string, int>(StringComparer.Ordinal);

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

                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (LicensedModuleDetailReads.ContainsKey(relative))
                {
                    licensedSeen[relative] = licensedSeen.GetValueOrDefault(relative) + 1;
                    continue;
                }

                offenders.Add($"  {relative}:{line}  {trimmed.Trim()}");
            }
        }

        // The license is a COUNT, not a blanket: a licensed file growing an extra read fails here, and a
        // licensed read that disappeared means the list is stale — both are worth a human look.
        foreach (var (file, expected) in LicensedModuleDetailReads)
        {
            Assert.True(licensedSeen.GetValueOrDefault(file) == expected,
                $"{file}: expected exactly {expected} licensed module-detail read(s), found {licensedSeen.GetValueOrDefault(file)} — "
                + "update LicensedModuleDetailReads deliberately (ADR 0767) rather than letting it drift.");
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

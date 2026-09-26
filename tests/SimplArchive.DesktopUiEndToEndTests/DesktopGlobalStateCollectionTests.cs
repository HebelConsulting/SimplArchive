using System.Text.RegularExpressions;

namespace SimplArchive.UiEndToEndTests;

/// <summary>
/// Every test class that writes process-global desktop state is in ONE collection (#1401).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The desktop client keeps a handful of process-global settables — the installation
/// it talks to, where its server profiles live, where it stages a temp file. A test that writes one and then
/// depends on it is racing every other class that writes the same thing, and xUnit runs classes in parallel
/// unless a collection says otherwise.
/// </para>
/// <para>
/// It failed exactly that way: <c>DesktopContentAddressTests</c> set <c>DesktopClientOptions.ApiBaseUrl</c> to
/// a loopback stand-in and made a real request through it, while a UI-collection test reassigned the same
/// global to the self-hosted Api mid-flight. The request went there instead and the assertion failed as a bare
/// <c>404</c> from a server it never meant to talk to — <b>once in a full suite run, 5/5 passing alone</b>,
/// which is the shape that reads as a code regression in whichever PR happens to be running.
/// </para>
/// <para>
/// There were then TWO collections doing this, in parallel with each other, which is the same bug one level
/// up. Now there is one, and this test is what keeps it that way: the fix that mattered was not moving a
/// class, it was making the next class impossible to forget.
/// </para>
/// </remarks>
public class DesktopGlobalStateCollectionTests
{
    /// <summary>
    /// The process-global settables. A write to any of these puts a class in the collection.
    /// </summary>
    /// <remarks>
    /// Listed rather than discovered, because "a static settable property on the desktop client" would sweep in
    /// every option object and produce a guard nobody believes. These are the ones a test actually writes to
    /// steer the client, and the list is short enough to extend deliberately when a new one appears — which is
    /// the moment to ask whether it should be global at all.
    /// </remarks>
    private static readonly string[] Globals =
    [
        "DesktopClientOptions.ApiBaseUrl",
        "ServerProfileStore.PathOverride",
        "NativeFileOpener.TempDirectoryOverride",
        "ApiCore.Authenticated",
        "EnvelopeOpener.Keys",
        "EnvelopeOpener.Openers",
        "CardCertificates.Reader",
        "CardCertificates.ModulePathOverride",
        "CardSession.PinPrompt",
    ];

    [Fact]
    public void A_class_that_writes_process_global_state_is_in_the_UI_collection()
    {
        var offenders = new List<string>();
        var writesAGlobal = new Regex(
            @"(" + string.Join("|", Globals.Select(Regex.Escape)) + @")\s*=[^=]", RegexOptions.Compiled);

        foreach (var file in Directory.EnumerateFiles(TestSourceDirectory(), "*.cs"))
        {
            // THIS file is skipped, and it is not a carve-out for convenience: it holds every global's name as
            // a string literal, and the anti-vacuous test below holds example ASSIGNMENTS as literals too. A
            // scanner that reads its own test data flags itself — which it did, first run. Naming the file is
            // honest; teaching the regex to ignore string literals would be a parser, and a guard that needs
            // one has stopped being cheap enough to trust.
            if (Path.GetFileName(file) == $"{nameof(DesktopGlobalStateCollectionTests)}.cs")
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (!writesAGlobal.IsMatch(text) || text.Contains($"[Collection({nameof(UiCollection)}.Name)]", StringComparison.Ordinal))
            {
                continue;
            }

            offenders.Add(Path.GetFileName(file));
        }

        Assert.True(offenders.Count == 0,
            "These test classes write process-global desktop state but are not in the UI collection, so they "
            + "run in PARALLEL with every class that writes the same thing:\n  "
            + string.Join("\n  ", offenders)
            + $"\n\nAdd [Collection({nameof(UiCollection)}.Name)]. It costs nothing when no test in the class "
            + "injects the fixture — the app is not stood up — and it is what stops an intermittent failure "
            + "that reads as a code regression in whichever PR is running (#1401).");
    }

    [Fact]
    public void The_scanner_still_recognises_a_write_and_ignores_a_read()
    {
        // Anti-vacuous, and specific about the boundary: a comparison is not a write, and the guard would be
        // worse than useless if it flagged one — a guard wrong more often than right gets suppressed.
        var writesAGlobal = new Regex(
            @"(" + string.Join("|", Globals.Select(Regex.Escape)) + @")\s*=[^=]", RegexOptions.Compiled);

        Assert.Matches(writesAGlobal, "DesktopClientOptions.ApiBaseUrl = installation.BaseUrl;");
        Assert.Matches(writesAGlobal, "ApiCore.Authenticated  =  signedIn;");
        Assert.DoesNotMatch(writesAGlobal, "if (DesktopClientOptions.ApiBaseUrl == expected)");
        Assert.DoesNotMatch(writesAGlobal, "var url = DesktopClientOptions.ApiBaseUrl;");
    }

    /// <summary>This suite's own source directory, found from the assembly rather than assumed.</summary>
    private static string TestSourceDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SimplArchive.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "tests", "SimplArchive.DesktopUiEndToEndTests");
    }
}

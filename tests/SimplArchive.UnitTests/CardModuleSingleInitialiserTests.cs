using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// EXACTLY ONE PKCS#11 library may exist in a running desktop client (#1490, ADR 0832).
//
// WHY THIS IS A TEST AND NOT A COMMENT. `C_Initialize` is per PROCESS. `CardSession` once owned a library of
// its own, and that second initialiser produced CKR_CRYPTOKI_ALREADY_INITIALIZED the first time a real
// preview read the card from several tasks at once — a preview fires page render, thumbnails and text layout
// concurrently. The fix was structural (the module belongs to CardModule, everything else runs inside its
// gate) and the invariant then lived only in a <remarks> block, where nothing enforces it.
//
// The failure mode is what makes prose insufficient: a second initialiser is invisible in every sequential
// test and breaks the first time two reads overlap on real hardware, which is where nobody looks until a
// demonstration. That is not hypothetical — it is how this reached one (#1408).
//
// A RUNTIME test was considered and rejected. Two initialisers colliding needs a real module and a real card;
// SoftHSM would prove things about SoftHSM (its OAEP is SHA-1 only and it segfaults on a mechanism/key
// mismatch); and a per-process fact cannot be honestly reproduced in a test host that shares its process with
// other card tests. So this asserts the shape of the source instead, which is the part a person changes.
public partial class CardModuleSingleInitialiserTests
{
    // THE one initialiser. Everything that needs the card goes through this class's gate.
    private const string TheModule = "Services/CardModule.cs";

    // Sites that construct a library of their own and are safe ANYWAY, each with the reason — and the reason
    // has to be about WHEN THE CODE RUNS, not about what the file is called. A diagnostic is safe because it
    // owns its process; a service that merely feels peripheral is not.
    //
    // This list may only get shorter. A new entry needs the same scrutiny as a new initialiser.
    private static readonly Dictionary<string, string> OwnProcess = new(StringComparer.Ordinal)
    {
        ["Services/CardEnvelopeCheck.cs"] =
            "The --card-envelope-test diagnostic. Invoked from Program's argument handling, which returns "
            + "without building the app, so CardModule is never loaded in that process.",
    };

    [Fact]
    public void Nothing_but_the_module_initialises_pkcs11_unless_it_owns_its_process()
    {
        var (_, desktop) = Paths();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
            .Where(NotGenerated)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            if (!Constructs(File.ReadAllText(file)))
            {
                continue;
            }

            var relative = Path.GetRelativePath(desktop, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative != TheModule && !OwnProcess.ContainsKey(relative))
            {
                offenders.Add($"  {relative}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These construct a Pkcs11Library of their own:\n" + string.Join("\n", offenders)
            + $"\n\nC_Initialize is per PROCESS, so a second one throws CKR_CRYPTOKI_ALREADY_INITIALIZED as "
            + "soon as two card reads overlap — which a sequential test never sees (#1490, ADR 0832). Reach "
            + $"the card through {TheModule}'s gate instead. If the code genuinely owns its process (a "
            + "diagnostic invoked from Program that returns before the app is built), add it to OwnProcess "
            + "WITH THAT REASON, in this same commit.");
    }

    [Fact]
    public void An_own_process_exemption_is_reachable_only_from_the_command_line()
    {
        var (_, desktop) = Paths();
        var contradicted = new List<string>();

        // The exemption's reason is "it owns its process", and what makes that true is that nothing in the
        // running APP calls it. So the claim is checked rather than taken: the only file naming the type may
        // be Program.cs, where the argument handling lives. A new caller anywhere else means the file now
        // runs alongside CardModule and its exemption has quietly become false.
        foreach (var (relative, _) in OwnProcess)
        {
            var type = Path.GetFileNameWithoutExtension(relative);
            var callers = Directory.EnumerateFiles(desktop, "*.cs", SearchOption.AllDirectories)
                .Where(NotGenerated)
                .Where(f => !Path.GetFileName(f).Equals($"{type}.cs", StringComparison.Ordinal))
                .Where(f => Regex.IsMatch(File.ReadAllText(f), $@"\b{Regex.Escape(type)}\b"))
                .Select(f => Path.GetRelativePath(desktop, f).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(f => f != "Program.cs")
                .ToList();

            if (callers.Count > 0)
            {
                contradicted.Add($"  {relative} is now named by: {string.Join(", ", callers)}");
            }
        }

        Assert.True(contradicted.Count == 0,
            "These are exempted from the single-initialiser rule because they own their process, and "
            + "something other than Program.cs now calls them:\n" + string.Join("\n", contradicted)
            + "\n\nIf they can run inside the app, they share the process with CardModule and the exemption "
            + "is false — take the library from the module's gate instead of constructing one.");
    }

    [Fact]
    public void The_scanner_still_finds_the_modules_own_initialiser()
    {
        // The anti-vacuous half, and it earns its place: this guard's whole content is a pattern over source,
        // so a renamed type or a reformatted constructor would make both tests above pass while watching
        // nothing at all.
        var (_, desktop) = Paths();
        var module = Path.Combine(desktop, TheModule.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(module),
            $"{TheModule} has moved, so this guard is no longer watching the one sanctioned initialiser.");
        Assert.True(Constructs(File.ReadAllText(module)),
            $"The pattern no longer matches {TheModule}'s own construction, so the guard above would pass "
            + "whatever the rest of the client does. Update the pattern, not this assertion.");
    }

    private static (string Root, string Desktop) Paths()
    {
        var root = RepoPaths.Root();
        return (root, Path.Combine(root, "src", "SimplArchive.DesktopClient"));
    }

    private static bool NotGenerated(string file) =>
        !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    // Comment lines are skipped, so the prose above — and the explanations at each site — do not count as
    // initialisers. A guard that punished documenting itself would teach people to stop.
    private static bool Constructs(string source) => source
        .Split('\n')
        .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
        .Any(line => Initialiser().IsMatch(line));

    // The optional namespace prefix is not decoration: the first version of this pattern required an
    // UNQUALIFIED `new Pkcs11Library(`, and a deliberately planted `new CAManagement.Pkcs11.Pkcs11Library(`
    // walked past it. The guard passed, which is how a guard becomes decoration — so the negative check that
    // found it is worth keeping in mind before narrowing this again.
    [GeneratedRegex(@"new\s+(?:[\w.]+\.)?Pkcs11Library\s*\(")]
    private static partial Regex Initialiser();
}

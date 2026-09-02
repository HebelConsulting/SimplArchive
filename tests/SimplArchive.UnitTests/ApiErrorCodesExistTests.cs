using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every error code `ApiErrorText` maps must be one the API can actually emit.
//
// This exists because two of the original ten mappings could never fire. They were written from the exception
// CLASS names rather than the codes those classes carry: `DocumentUnderLegalHoldException` emits `LEGAL_HOLD`,
// not `DOCUMENT_UNDER_LEGAL_HOLD`, and the invalid-transition one emits `INVALID_WORKFLOW_TRANSITION`. Both
// mappings sat there looking correct and did nothing.
//
// The failure is invisible by construction, which is what makes it worth a guard rather than care: an unmapped
// code does not throw or blank out — it falls through to a perfectly sensible generic sentence ("the server
// refused the action"), so the user sees plausible German and nobody notices the specific message never
// appeared. It took provoking one of those exact refusals in a test to discover it, and only then because the
// test asserted the SPECIFIC sentence rather than "some German text".
//
// The reverse direction is deliberately NOT asserted: the API emits ~126 codes and the clients surface a
// handful, so an unmapped code is normal and correct, not a gap to be filled.
public partial class ApiErrorCodesExistTests
{
    // `=> Strings.Get(...)` distinguishes a mapped code from the `_` fallback arm.
    [GeneratedRegex(@"""(?<code>[A-Z_0-9]+)""\s*=>\s*Strings\.Get")]
    private static partial Regex MappedCode();

    // How an ApiException subclass declares its code — in EITHER of the two shapes the codebase uses:
    //
    //     : base("SOME_CODE", StatusCodes...)            an explicit constructor body
    //     : SomeAreaException("SOME_CODE", StatusCodes…) a primary constructor's base list
    //
    // Only the first was matched until #667, so every primary-constructor exception was invisible here and the
    // test would report its code as one "the API never emits". That is a FALSE NEGATIVE in the guard, and the
    // dangerous kind: it fires on correct code and names the wrong culprit, so the obvious fix is to change the
    // exception rather than the scanner. The newer exceptions in this codebase are overwhelmingly the second
    // shape, so the blind spot was growing.
    [GeneratedRegex(@"(?:base|:\s*\w*Exception)\(\s*""(?<code>[A-Z_0-9]+)""")]
    private static partial Regex EmittedCode();

    [Fact]
    public void Every_code_the_clients_map_is_one_the_api_emits()
    {
        var root = RepoPaths.Root();
        var mapText = File.ReadAllText(Path.Combine(root, "src", "SimplArchive.Localization", "ApiErrorText.cs"));
        var mapped = MappedCode().Matches(mapText).Select(m => m.Groups["code"].Value).ToList();

        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var apiDir = Path.Combine(root, "src", "SimplArchive.Api");
        foreach (var file in Directory.EnumerateFiles(apiDir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (Match m in EmittedCode().Matches(File.ReadAllText(file)))
            {
                emitted.Add(m.Groups["code"].Value);
            }
        }

        // Anti-vacuous, and not a function of how much work is left (see LocalizationLiteralTests): if either
        // regex stopped matching, the comparison below would pass while checking nothing.
        Assert.True(mapped.Count > 0, "No mapped codes found in ApiErrorText — the mapping shape changed.");
        Assert.True(emitted.Count > 50, $"Only {emitted.Count} API error codes found — the scan is probably broken.");

        var dead = mapped.Where(c => !emitted.Contains(c)).ToList();

        Assert.True(dead.Count == 0,
            "ApiErrorText maps error codes the API never emits, so they can never fire and the user silently gets "
            + "the generic sentence instead:\n"
            + string.Join("\n", dead.Select(c => $"  {c}"))
            + "\n\nUse the code the exception actually carries — its `: base(\"CODE\", ...)` argument — not the "
            + "class name.");
    }

}

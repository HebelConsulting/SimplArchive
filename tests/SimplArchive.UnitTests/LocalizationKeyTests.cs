using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Guard for localisation keys (ADR 0468/0470/0471 established the resources; this makes using them safe).
//
// `Strings.Get("SomeKey")` and `{loc:Tr SomeKey}` both take a key as a STRING, so a typo or an invented name is
// invisible to the compiler and shows up as a blank label at runtime — in whichever language nobody happened to
// click through. Twice while building external links I referenced keys that did not exist (`GenericError`,
// `CloseBtn`, `GroupsLabel`) and found them only by grepping.
//
// This turns that into a build failure, and also checks the four languages agree: a key present in English but
// missing from German is a silently-untranslated string, which is the same failure one step later.
public class LocalizationKeyTests
{
    private static readonly Regex GetCall = new(@"Strings\.Get\(""([^""]+)""\)", RegexOptions.Compiled);
    private static readonly Regex TrMarkup = new(@"\{loc:Tr\s+([A-Za-z0-9_]+)\s*\}", RegexOptions.Compiled);
    private static readonly Regex ResxKey = new(@"<data name=""([^""]+)""", RegexOptions.Compiled);

    [Fact]
    public void Every_referenced_string_key_exists()
    {
        var root = RepoRoot();
        var defined = KeysIn(Path.Combine(root, "src", "SimplArchive.Localization", "Strings.resx"));

        var missing = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            // Comments are stripped first: documentation legitimately shows the USAGE of a key ("{loc:Tr SomeKey}"
            // in TrExtension's own summary), and flagging those would make the guard cry wolf until somebody
            // switched it off — which is how a useful check becomes a disabled one.
            var text = WithoutComments(File.ReadAllText(file));
            var referenced = GetCall.Matches(text).Select(m => m.Groups[1].Value)
                .Concat(TrMarkup.Matches(text).Select(m => m.Groups[1].Value));

            foreach (var key in referenced.Distinct().Where(k => !defined.Contains(k)))
            {
                missing.Add($"  {Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')}: \"{key}\"");
            }
        }

        Assert.True(missing.Count == 0,
            "These localisation keys are referenced but not defined in Strings.resx. The compiler cannot catch "
            + "this — the key is a string — so it would render as a blank label at runtime:\n"
            + string.Join("\n", missing.Distinct().OrderBy(m => m)));
    }

    // A key defined in English but absent from a translation renders blank for those users. Catching it here is
    // cheaper than noticing it in a screenshot, and cheaper still than a user noticing.
    [Theory]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("es")]
    public void Every_translation_defines_every_key(string culture)
    {
        var root = RepoRoot();
        var english = KeysIn(Path.Combine(root, "src", "SimplArchive.Localization", "Strings.resx"));
        var translated = KeysIn(Path.Combine(root, "src", "SimplArchive.Localization", $"Strings.{culture}.resx"));

        var missing = english.Except(translated).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0,
            $"Strings.{culture}.resx is missing {missing.Count} key(s) that English defines, so they render blank "
            + $"for {culture} users:\n  " + string.Join("\n  ", missing));
    }

    // Drops // line comments, /* */ blocks and Razor @* *@ blocks. Deliberately simple: it does not try to
    // understand string literals containing "//", because the only cost of over-stripping here is missing a
    // reference, and a missed reference fails loudly at runtime rather than silently passing a security gate.
    private static string WithoutComments(string text)
    {
        text = Regex.Replace(text, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^\s*//.*$", " ", RegexOptions.Multiline);
        return text;
    }

    private static HashSet<string> KeysIn(string resxPath) =>
        ResxKey.Matches(File.ReadAllText(resxPath)).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> SourceFiles(string root)
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
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (Path.GetExtension(file) is ".cs" or ".razor" or ".axaml")
                {
                    yield return file;
                }
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SimplArchive.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root (SimplArchive.slnx).");
    }
}

using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Enforces the standing "all S3 key prefixes are parametrized constants in one class-file" principle (CLAUDE.md):
// the bucket layout — the fixed tenants/… path structure — is defined only in ObjectKeyPrefixes, and no other
// source file writes a "tenants/…" prefix literal. Everything else composes keys from ObjectKeyPrefixes' methods,
// so the layout has one home and one thing to change when it moves. A stray literal fails the build here.
public class ObjectKeyPrefixesTests
{
    // A string literal that opens an object-key prefix — "tenants/ or $"tenants/. The opening quote is what
    // separates a KEY from prose that merely describes the layout (a doc comment names it without a quote), and
    // from an unrelated route like "api/tenants" (no slash immediately after the quote).
    private static readonly Regex KeyPrefixLiteral = new(@"\$?""tenants/", RegexOptions.Compiled);

    private const string TheOneClass = "ObjectKeyPrefixes.cs";

    [Fact]
    public void Only_ObjectKeyPrefixes_contains_an_object_key_prefix_literal()
    {
        var root = RepoPaths.Root();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != TheOneClass)
            .Where(f => KeyPrefixLiteral.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(root, f))
            .OrderBy(f => f)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "An object-storage key PREFIX literal (\"tenants/…\") may live only in "
            + "src/SimplArchive.Application/Abstractions/ObjectKeyPrefixes.cs — every other file must compose its "
            + "keys from that class's parametrized methods (CLAUDE.md: \"all S3 key prefixes are parametrized "
            + "constants in one class-file\"). Add or reuse a prefix there instead. Offending files:\n"
            + string.Join("\n", offenders));
    }
}

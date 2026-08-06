using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Anti-regression guard for the public-mirror boundary (ADR 0484). This repository is the private canonical and
// publishes a FILTERED mirror to the public one: `docs/`, `CLAUDE.md`, `tools/`, `publish/` and `README.md` are
// withheld by path, and everything else — `src/`, `tests/`, `charts/`, `scripts/`, `.github/workflows/`, the root
// config and the solution file — is published BYTE FOR BYTE.
//
// The publish step refuses to run if the shared tree names the commercial DMS this project is modelled on. That
// gate is correct, but it only fires in the publish workflow, on `main`, AFTER a merge — so a stray mention in a
// code comment costs a red pipeline and a pile of CI minutes to discover something a local `dotnet test` could
// have caught in milliseconds. This test is that local check: same rule, same pattern, shifted left.
//
// When it fails: reword the comment. The concept is almost always expressible without the brand — "external
// system", "interop layer", "the external DMS" — and the private-only ADRs under `docs/` are where the specific
// product belongs anyway.
public class PublicMirrorBoundaryTests
{
    // The forbidden token is ASSEMBLED rather than written out: spelling it literally would make this very file
    // trip the gate it exists to enforce (this file is under tests/, which is published). Mirrors ELO_PATTERN in
    // publish/publish-public.sh — the bare word, the domain, and the XML-format name.
    //
    // Declared first because the withheld-file list below has to be built from it for the same reason — the first
    // run of this test failed on its own source, which is a fair demonstration that the rule has teeth.
    private static readonly string Brand = "e" + "l" + "o";

    // Paths published byte-for-byte to the public repo. Kept in step with WITHHELD in publish/publish-public.sh:
    // anything not withheld there is published, and therefore in scope here.
    private static readonly string[] PublishedRoots =
        ["src", "tests", "charts", "scripts", ".github/workflows"];

    // Two workflow files live under a published root but are themselves withheld by name.
    private static readonly string[] WithheldFiles =
        [$".github/workflows/{Brand}xml-tool.yml", ".github/workflows/auto-publish.yml"];

    private static readonly Regex Forbidden =
        new($@"(\b{Brand}\b)|({Brand}\.com)|({Brand}xml)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void No_published_file_names_the_commercial_dms()
    {
        var root = RepoRoot();
        var withheld = WithheldFiles
            .Select(f => Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offenders = new List<string>();

        foreach (var published in PublishedRoots)
        {
            var dir = Path.Combine(root, published.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                    || withheld.Contains(file)
                    || IsProbablyBinary(file))
                {
                    continue;
                }

                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (Forbidden.IsMatch(lines[i]))
                    {
                        offenders.Add($"  {Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')}:{i + 1}: {lines[i].Trim()}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "These PUBLISHED files name the commercial DMS, which the public-mirror publish step rejects (ADR 0484). "
            + "Reword them — say \"external system\" / \"interop layer\" — and keep the specifics in the private "
            + "docs/ ADRs. Catching this here costs milliseconds; catching it in the publish workflow costs a red "
            + "pipeline on main.\n"
            + string.Join("\n", offenders));
    }

    // Cheap stand-in for grep -I: skip anything with a NUL byte in its first block.
    private static bool IsProbablyBinary(string path)
    {
        if (Path.GetExtension(path) is ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".pdf" or ".zip"
            or ".dll" or ".exe" or ".woff" or ".woff2" or ".ttf" or ".eot" or ".tif" or ".tiff")
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(path);
            Span<byte> head = stackalloc byte[512];
            var read = stream.Read(head);
            return head[..read].IndexOf((byte)0) >= 0;
        }
        catch (IOException)
        {
            return true; // unreadable → not our problem to scan
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

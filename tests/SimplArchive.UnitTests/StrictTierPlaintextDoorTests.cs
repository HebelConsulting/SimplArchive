using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// The strict tier's one property: content does not leave as plaintext (#1376, ADR 0825).
//
// WHY THIS IS A SEAM TEST AND NOT TWENTY CALL-SITE TESTS. Twenty files in the Api hand out document bytes or
// presigned URLs to them — CalDAV, checkouts, archives, contact cards, item sources, bulk, external links,
// the intray, modules, IMAP, WebDAV, the finalizer, the exporter, the version resource builder. A property
// that holds at nineteen of them is not a property: an attacker uses the twentieth, and so does a door added
// next year by somebody who never read this ADR.
//
// IObjectStorageClient is the single seam all of them pass through, and CLAUDE.md already forbids adding a
// storage path that bypasses it. So the assertion is on the seam: nothing in src/ may presign document content
// except through that interface.
public class StrictTierPlaintextDoorTests
{
    // A direct AWS presign outside the storage client would be a door the strict tier cannot close, because
    // the refusal lives in the decorator over that interface.
    private static readonly Regex DirectPresign = new(
        @"GetPreSignedURL|GetPreSignedUrlRequest", RegexOptions.Compiled);

    [Fact]
    public void Only_the_storage_client_presigns_object_urls()
    {
        var root = RepoPaths.Root();
        var storageClient = Path.Combine("SimplArchive.Infrastructure", "Storage", "S3ObjectStorageClient.cs");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                // The one implementation allowed to presign: it IS the seam's inner half, and the strict
                // refusal sits in the decorator wrapping it.
                && !f.EndsWith(storageClient, StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f)
                .Select((line, index) => (File: f, Number: index + 1, Text: line))
                .Where(l => !l.Text.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && !l.Text.TrimStart().StartsWith("///", StringComparison.Ordinal))
                .Where(l => DirectPresign.IsMatch(l.Text)))
            .Select(l => $"  {Path.GetRelativePath(root, l.File).Replace(Path.DirectorySeparatorChar, '/')}:{l.Number}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "These lines presign an object URL outside S3ObjectStorageClient, so they bypass the seam where "
            + "the strict tier refuses to serve plaintext (#1376). Every content path must go through "
            + "IObjectStorageClient:\n" + string.Join("\n", offenders));
    }

    // The anti-vacuous half. A scanner that matches nothing passes forever, including on the day somebody
    // renames the AWS call — so prove the pattern still finds the one legitimate implementation.
    [Fact]
    public void The_scanner_still_recognises_a_presign()
    {
        var seam = Path.Combine(RepoPaths.Root(), "src", "SimplArchive.Infrastructure", "Storage", "S3ObjectStorageClient.cs");

        Assert.True(File.Exists(seam), "S3ObjectStorageClient has moved — this guard is no longer watching the seam.");
        Assert.True(DirectPresign.IsMatch(File.ReadAllText(seam)),
            "The presign pattern no longer matches the one class that legitimately presigns, so the guard above "
            + "would pass whatever the rest of src/ does. Update the pattern, not this assertion.");
    }
}

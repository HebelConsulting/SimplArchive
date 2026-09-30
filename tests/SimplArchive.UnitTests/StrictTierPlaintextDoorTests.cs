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

    // WHAT THE SEAM TEST ABOVE CANNOT SEE, which is how #1485 happened.
    //
    // The seam catches a door that hands out object BYTES or a URL to them. It is blind to a door that reads
    // through the server-side path and answers something DERIVED — because that never touches the
    // client-facing read the decorator refuses on, and ADR 0857 says so in as many words: such doors "must
    // say so themselves".
    //
    // Version comparison is the worst of that family: it answers with both sides' full plain text (ADR
    // 0712). It shipped ungated for months, 62 lines from the gate its sibling overlay received, and no test
    // in this file could have noticed. So the consumers of the comparer are enumerated here — narrowly and
    // by name, rather than as a general theory of derived doors, because a guard aimed at everything would
    // be a guard nobody can calibrate (and this one is already the second attempt at that lesson).
    [Fact]
    public void Every_consumer_of_the_comparer_refuses_where_plaintext_doors_refuse()
    {
        var root = RepoPaths.Root();

        var consumers = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("IDocumentVersionComparer", StringComparison.Ordinal)
                // Not doors: the interface's own declaration, its implementation, and the line that
                // REGISTERS it. The registration was this guard's first false positive — a reminder that a
                // scan for a type name finds every mention of it, and a guard wrong on its first run is one
                // that gets suppressed rather than read.
                && !Path.GetFileName(f).Equals("IDocumentVersionComparer.cs", StringComparison.Ordinal)
                && !Path.GetFileName(f).Equals("DocumentVersionComparer.cs", StringComparison.Ordinal)
                && !Path.GetFileName(f).Equals("DependencyInjection.cs", StringComparison.Ordinal))
            .ToList();

        // Anti-vacuity, and it is not ceremony: had this test been written against one consumer it would
        // have passed while the other leaked. Two surfaces compare today — versions, and a check-out's
        // working copy — so a drop to one means a consumer was renamed out of the scan, not fixed.
        Assert.True(consumers.Count >= 2,
            $"Expected at least two files to consume the comparer; found {consumers.Count}. The scan has "
            + "stopped finding them, so this guard is no longer watching anything.");

        var ungated = consumers
            .Where(f => !File.ReadAllText(f).Contains("RefuseIfStrictAsync", StringComparison.Ordinal))
            .Select(f => "  " + Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();

        Assert.True(ungated.Count == 0,
            "These files hand a comparison to a caller without refusing on a tenant whose doors refuse "
            + "plaintext (#1485, ADR 0861). A comparison IS both versions' plain text, so each consumer must "
            + "call StrictEnvelopeDelivery.RefuseIfStrictAsync and withhold its `compare` rel:\n"
            + string.Join("\n", ungated));
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

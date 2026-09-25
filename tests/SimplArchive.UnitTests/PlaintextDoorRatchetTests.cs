using System.Text.RegularExpressions;

namespace SimplArchive.UnitTests;

// Every Api read of a document's bytes is either DECLARED as serving a client, or listed here as internal with
// a reason (#1394, ADR 0829).
//
// WHY THIS EXISTS. #1376 put the strict tier's refusal at the storage seam on the argument that "a property
// that holds at 19 of 20 doors is not a property" — and put it on the PRESIGN. Twelve doors never presign:
// they read bytes server-side and stream them. WebDAV was even listed in that issue as a blind door that is
// refused, and was not refused; the decision was recorded and the enforcement was never written. Nothing
// failed, which is the whole problem — a missing refusal is invisible until somebody greps for it.
//
// So the seam now has two reads: GetObjectAsync for our own purposes, and GetObjectForClientAsync for bytes
// about to be handed to somebody. The second refuses for a strict tenant. This guard is what keeps the
// distinction honest, because the compiler cannot: both return a Stream, and forgetting which one you meant
// produces working code that quietly serves plaintext.
public partial class PlaintextDoorRatchetTests
{
    // Reads that are NOT a door — the bytes never reach a client — each with why. This list may only get
    // shorter, and a new entry needs the same scrutiny as a new door: "it is internal" is a claim about where
    // the bytes GO, not about what the method is called.
    private static readonly Dictionary<string, string> InternalReaders = new(StringComparer.Ordinal)
    {
        ["Documents/DocumentFinalizer.cs"] = "Hashes and classifies what was just uploaded; the bytes are compared and discarded.",
        ["Documents/CalendarContactClassifier.cs"] = "Reads an .ics/.vcf to decide what the document IS. Index fields, not content.",
        ["Documents/StrictEnvelopeDelivery.cs"] = "THE envelope path — it reads plaintext precisely so the reader never does (ADR 0828).",
        ["Controllers/ExternalLinksController.cs"] = "Reads plaintext to ENVELOPE it to the link's recipient certificate (ADR 0827) — the same shape as StrictEnvelopeDelivery. Its other path hands out a presigned URL, which the seam already refuses for a strict tenant.",
        ["Imap/ImapFetch.cs"] = "Envelopes to the recipient's certificate before serving (#1332), which is the precedent the tier is modelled on.",
        ["Controllers/CheckoutsController.cs"] = "Hashes the stash to answer 'is it modified'; no bytes are served.",
        ["Controllers/IntrayController.cs"] = "Reads the mask SIDECAR, which is metadata the tier serves anyway — not the item's content.",
        ["Controllers/ModulesController.cs"] = "Reads a module LICENCE, which is a signed claim about the tenant rather than a document.",
        ["WebDav/WebDavSpecialHandlers.cs"] = "Server-side copy of a scratch object back into storage; the bytes do not leave.",
    };

    [Fact]
    public void Every_api_read_of_document_bytes_is_declared_or_listed()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        var api = Path.Combine(root, "src", "SimplArchive.Api");
        var undeclared = new List<string>();

        foreach (var file in Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            // The ForClient forms contain the plain names as substrings, so they are excluded explicitly —
            // without that this guard would flag every door it exists to approve of.
            var reads = PlainRead().Matches(File.ReadAllText(file)).Count;
            if (reads == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(api, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!InternalReaders.ContainsKey(relative))
            {
                undeclared.Add($"  {relative}");
            }
        }

        Assert.True(undeclared.Count == 0,
            "These read a document's bytes without declaring whether they are serving a client:\n"
            + string.Join("\n", undeclared)
            + "\n\nIf the bytes are about to be handed to somebody, call GetObjectForClientAsync(key, \"<the "
            + "door>\", ct) — the strict tier refuses it, because that door cannot carry an encrypted envelope "
            + "(ADR 0829). If they never leave this process, add the file to InternalReaders WITH THE REASON, "
            + "in this same commit. 'It is internal' is a claim about where the bytes GO.");
    }

    [Fact]
    public void The_list_does_not_rot()
    {
        if (PrivateRepositoryGate.RepoRoot() is not { } root)
        {
            return;
        }

        // The other direction, which is what makes it a ratchet: an entry whose file no longer reads bytes is
        // an exemption nobody is using, and it would silently excuse the next thing to appear at that path.
        var api = Path.Combine(root, "src", "SimplArchive.Api");
        var stale = InternalReaders.Keys
            .Where(relative =>
            {
                var file = Path.Combine(api, relative.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(file) || PlainRead().Matches(File.ReadAllText(file)).Count == 0;
            })
            .ToList();

        Assert.True(stale.Count == 0,
            "These are listed as internal readers but no longer read document bytes: "
            + string.Join(", ", stale) + ". Remove their entries.");
    }

    // GetObjectAsync / GetObjectRangeAsync / GetObjectWithMetadataAsync, but NOT the ForClient forms — the
    // negative lookahead is what stops a declared door being reported as an undeclared one.
    [GeneratedRegex(@"\.GetObject(?!ForClient|RangeForClient)(Range|WithMetadata)?Async\s*\(")]
    private static partial Regex PlainRead();
}

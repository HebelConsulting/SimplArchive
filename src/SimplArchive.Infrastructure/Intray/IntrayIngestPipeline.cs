using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Infrastructure.Intray;

/// <summary>
/// What happens to a file on its way into the intray: each enabled processor gets a turn, <b>in a stated
/// order</b>, exactly once per item (issue #494).
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the reason this exists.</b> Straightening (#491) has to run before patch-code detection
/// (#492): a patch code is horizontal bars at defined widths, found by reading a projection profile across the
/// page, and two degrees of rotation smears them across scan lines until the profile flattens. Built as two
/// independent features the order would be whatever the code happened to fall into, and the failure is
/// silent — a batch that simply does not split, with nothing to explain why. Registration order is therefore a
/// decision, written down here and in the ADR, not an accident of call sequence.
/// </para>
/// <para>
/// It lives in Infrastructure rather than the Api because it has <b>two</b> callers that cannot see each other:
/// the endpoint a client calls after its upload, and the Worker's backstop sweep. The Worker does not reference
/// the Api, and the sweep is not optional — the intray is also a WebDAV mount (ADR 0509), so files arrive with
/// no client involved at all, and a client-signalled hook alone would quietly not apply to a whole ingest path.
/// </para>
/// <para>
/// <b>Exactly once</b> is the marker sidecar's job: an item that has been through the pipeline carries
/// <c>{name}.ingest.json</c> beside it, in the same scope as the staged-mask sidecar and hidden from the
/// listing the same way. Without it the sweep would reprocess every item on every pass, forever, converting
/// what it already converted.
/// </para>
/// </remarks>
public sealed class IntrayIngestPipeline(
    IObjectStorageClient storage,
    IEnumerable<IIntrayIngestProcessor> processors,
    ILogger<IntrayIngestPipeline> logger)
{
    /// <summary>The marker that says this item has already been through. Hidden from the intray listing.</summary>
    public const string MarkerSuffix = ".ingest.json";

    /// <summary>
    /// Left beside an item whose content carries a digital signature (#491) — zero bytes, because its EXISTENCE
    /// is the whole message.
    /// </summary>
    /// <remarks>
    /// A sidecar rather than a field, so the listing gets the answer for free: it already enumerates the prefix
    /// to build the rows, exactly as `hasMask` is answered today. Reading each item's bytes to paint a list
    /// would cost one download per row, which is the price this whole design keeps refusing to pay.
    /// </remarks>
    public const string SignedSuffix = ".signed";

    private const string MaskSidecarSuffix = IntrayNaming.MaskSidecarSuffix;

    /// <summary>
    /// Runs the pipeline over one staged item, and answers with what is in the intray afterwards.
    /// </summary>
    /// <remarks>
    /// Usually that is the same one item, under the same name or a new one when a processor changed the
    /// format. It is <b>several</b> when a processor cut the item up (#492), and <b>empty</b> when nothing ran
    /// at all — a sidecar, an item already processed, or one that has since been deleted.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RunAsync(
        Guid tenantId,
        Guid userId,
        string prefix,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (name.EndsWith(MarkerSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(SignedSuffix, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(MaskSidecarSuffix, StringComparison.OrdinalIgnoreCase)
            || await storage.ExistsAsync($"{prefix}{name}{MarkerSuffix}", cancellationToken))
        {
            return []; // a sidecar, or already done
        }

        if (!await storage.ExistsAsync(prefix + name, cancellationToken))
        {
            return []; // an upload that never completed, or an item deleted since
        }

        var bytes = await ReadAsync(prefix + name, cancellationToken);

        // A digitally signed document is left completely alone — not one processor, not a re-save. A signature
        // covers a byte range, so ANY rewrite voids it, and the damage is silent: the file still opens and
        // still looks right, and only announces itself as broken when somebody tries to verify it. Marked as
        // seen so the sweep does not keep re-reading it.
        if (DigitalSignature.IsSigned(bytes))
        {
            logger.LogInformation(
                "Intray ingest skipped {Item}: it carries a digital signature, which any rewrite would void.",
                name);

            await using (var empty = new MemoryStream())
            {
                await storage.PutObjectAsync($"{prefix}{name}{SignedSuffix}", empty, "application/octet-stream", cancellationToken);
            }

            await MarkSeenAsync(prefix, name, cancellationToken);
            return [];
        }

        // The item, and then whatever it became. A processor is allowed to cut one item into several, and the
        // ones after it get a turn over each piece — which is why this is a list and not a running name.
        var items = new List<(string Name, byte[] Bytes)> { (name, bytes) };
        var ran = new List<string>();  // each processor at most once, however many items it acted on

        foreach (var processor in processors)
        {
            var next = new List<(string Name, byte[] Bytes)>();

            foreach (var item in items)
            {
                try
                {
                    if (await processor.TryProcessAsync(
                            new IntrayIngestContext(tenantId, userId, prefix, item.Name, item.Bytes), cancellationToken)
                        is not { } processed)
                    {
                        next.Add(item); // declined: not enabled, or not the kind of file it acts on
                        continue;
                    }

                    next.AddRange(await ReplaceAsync(prefix, item.Name, processed, cancellationToken));
                    if (!ran.Contains(processor.Name))
                    {
                        ran.Add(processor.Name);
                    }
                }
                catch (Exception e)
                {
                    // One processor failing must not deprive the item of the others, and must not leave it
                    // unmarked — an item that throws every pass would otherwise be retried by the sweep forever.
                    logger.LogWarning(e, "Intray ingest processor {Processor} failed for {Item}; continuing.", processor.Name, item.Name);
                    next.Add(item);
                }
            }

            items = next;
        }

        foreach (var item in items)
        {
            await MarkAsync(prefix, item.Name, ran, cancellationToken);
        }

        return items.Select(i => i.Name).ToList();
    }

    /// <summary>
    /// Writes what a processor produced, and answers with what is now in the intray in the source's place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One part replaces the item</b> — in place, or under a new name when the format changed, taking the
    /// staged mask sidecar with it, since the draft belongs to the item and not to its extension.
    /// </para>
    /// <para>
    /// <b>Several parts leave the source behind</b>, renamed with <see cref="IntrayNaming.ToBeDeletedSuffix"/>
    /// and marked as already processed. Deleting it would be the tidier-looking choice and the wrong one: a
    /// batch cut in the wrong place is recoverable only from the batch, and the user has not seen the result
    /// yet at the moment this runs.
    /// </para>
    /// </remarks>
    private async Task<List<(string Name, byte[] Bytes)>> ReplaceAsync(
        string prefix,
        string name,
        IntrayProcessed processed,
        CancellationToken cancellationToken)
    {
        var stem = IntrayNaming.Stem(name);

        if (processed.Parts is [var only])
        {
            var newName = stem + only.Extension;
            await WriteAsync(prefix + newName, only, cancellationToken);

            if (!string.Equals(newName, name, StringComparison.Ordinal))
            {
                await IntrayNaming.MoveMaskDraftAsync(storage, prefix, name, newName, cancellationToken);
                await storage.DeleteObjectAsync(prefix + name, cancellationToken);
            }

            return [(newName, only.Bytes)];
        }

        var written = new List<(string Name, byte[] Bytes)>(processed.Parts.Count);
        for (var i = 0; i < processed.Parts.Count; i++)
        {
            var part = processed.Parts[i];
            var candidate = $"{stem} ({i + 1}){part.Extension}";
            if (await IntrayNaming.FreeNameAsync(storage, prefix, candidate, cancellationToken) is not { } partName)
            {
                continue; // a thousand items of the same name: leave the batch alone rather than half-write it
            }

            await WriteAsync(prefix + partName, part, cancellationToken);
            await IntrayNaming.CopyMaskDraftAsync(storage, prefix, name, partName, cancellationToken);
            written.Add((partName, part.Bytes));
        }

        await RenameSourceAsync(prefix, name, cancellationToken);
        return written;
    }

    private async Task RenameSourceAsync(string prefix, string name, CancellationToken cancellationToken)
    {
        var kept = $"{IntrayNaming.Stem(name)}{IntrayNaming.ToBeDeletedSuffix}{Path.GetExtension(name)}";
        if (await IntrayNaming.FreeNameAsync(storage, prefix, kept, cancellationToken) is not { } keptName)
        {
            return; // leave it under its own name rather than lose it to a naming collision
        }

        await storage.CopyObjectAsync(prefix + name, prefix + keptName, cancellationToken);
        await IntrayNaming.MoveMaskDraftAsync(storage, prefix, name, keptName, cancellationToken);
        await storage.DeleteObjectAsync(prefix + name, cancellationToken);

        // Marked here rather than by the loop above: the source is no longer one of the items being carried
        // forward, and an unmarked file in an intray is one the sweep will pick up and cut all over again.
        await MarkSeenAsync(prefix, keptName, cancellationToken);
    }

    private async Task WriteAsync(string key, IntrayPart part, CancellationToken cancellationToken)
    {
        await using var payload = new MemoryStream(part.Bytes);
        await storage.PutObjectAsync(key, payload, part.ContentType, cancellationToken);
    }

    /// <summary>
    /// Marks an item as seen without running anything — how the sweep grandfathers items that predate the
    /// feature, so shipping it does not silently rewrite an intray full of files people already have.
    /// </summary>
    public Task MarkSeenAsync(string prefix, string name, CancellationToken cancellationToken = default) =>
        MarkAsync(prefix, name, [], cancellationToken);

    // The marker records WHICH processors ran, not merely that something did: when a user asks why their scan
    // is now a PDF, the answer has to be in the system rather than inferred from the extension.
    private async Task MarkAsync(
        string prefix,
        string name,
        IReadOnlyList<string> ran,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.SerializeToUtf8Bytes(new { processors = ran });
        await using var payload = new MemoryStream(marker);
        await storage.PutObjectAsync($"{prefix}{name}{MarkerSuffix}", payload, "application/json", cancellationToken);
    }

    private async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        await using var stream = await storage.GetObjectAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }
}

/// <summary>One step of the intray ingest pipeline (#494).</summary>
/// <remarks>
/// A processor returns null to decline — not enabled for this user, or not the kind of file it acts on — which
/// is the normal case and not a failure. When it does act it returns the new bytes AND the extension they
/// should carry, because a processor is allowed to change the format and the pipeline has to know: deskew
/// always emits PDF, while patch splitting keeps what it was given, and one must not silently undo what the
/// other preserved.
/// </remarks>
public interface IIntrayIngestProcessor
{
    /// <summary>Recorded in the marker, so what happened to an item is answerable later.</summary>
    string Name { get; }

    Task<IntrayProcessed?> TryProcessAsync(IntrayIngestContext context, CancellationToken cancellationToken);
}

public sealed record IntrayIngestContext(Guid TenantId, Guid UserId, string Prefix, string Name, byte[] Bytes);

/// <summary>What a processor made out of the item it was given: usually one file, sometimes several.</summary>
/// <remarks>
/// The plural case is patch-code cutting (#492), where one batch scan becomes one item per document in the
/// stack. Modelling it as a list rather than giving fan-out its own contract is what lets the processors after
/// it run over each piece — a rule the pipeline can state once, instead of one that holds only while the
/// fan-out happens to be last.
/// </remarks>
public sealed record IntrayProcessed(IReadOnlyList<IntrayPart> Parts)
{
    /// <summary>The ordinary case: the item rewritten, still one file.</summary>
    public IntrayProcessed(byte[] bytes, string extension, string contentType)
        : this([new IntrayPart(bytes, extension, contentType)])
    {
    }
}

public sealed record IntrayPart(byte[] Bytes, string Extension, string ContentType);

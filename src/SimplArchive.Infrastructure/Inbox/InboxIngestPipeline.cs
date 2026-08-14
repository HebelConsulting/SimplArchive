using System.Text.Json;
using Microsoft.Extensions.Logging;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Storage;

namespace SimplArchive.Infrastructure.Inbox;

/// <summary>
/// What happens to a file on its way into the inbox: each enabled processor gets a turn, <b>in a stated
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
/// the Api, and the sweep is not optional — the inbox is also a WebDAV mount (ADR 0509), so files arrive with
/// no client involved at all, and a client-signalled hook alone would quietly not apply to a whole ingest path.
/// </para>
/// <para>
/// <b>Exactly once</b> is the marker sidecar's job: an item that has been through the pipeline carries
/// <c>{name}.ingest.json</c> beside it, in the same scope as the staged-mask sidecar and hidden from the
/// listing the same way. Without it the sweep would reprocess every item on every pass, forever, converting
/// what it already converted.
/// </para>
/// </remarks>
public sealed class InboxIngestPipeline(
    IObjectStorageClient storage,
    IEnumerable<IInboxIngestProcessor> processors,
    ILogger<InboxIngestPipeline> logger)
{
    /// <summary>The marker that says this item has already been through. Hidden from the inbox listing.</summary>
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

    private const string MaskSidecarSuffix = ".mask.json";

    /// <summary>
    /// Runs the pipeline over one staged item. Returns the item's name afterwards — which differs from the one
    /// passed in when a processor changed the format — or null when nothing ran.
    /// </summary>
    public async Task<string?> RunAsync(
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
            return null; // a sidecar, or already done
        }

        if (!await storage.ExistsAsync(prefix + name, cancellationToken))
        {
            return null; // an upload that never completed, or an item deleted since
        }

        var bytes = await ReadAsync(prefix + name, cancellationToken);

        // A digitally signed document is left completely alone — not one processor, not a re-save. A signature
        // covers a byte range, so ANY rewrite voids it, and the damage is silent: the file still opens and
        // still looks right, and only announces itself as broken when somebody tries to verify it. Marked as
        // seen so the sweep does not keep re-reading it.
        if (DigitalSignature.IsSigned(bytes))
        {
            logger.LogInformation(
                "Inbox ingest skipped {Item}: it carries a digital signature, which any rewrite would void.",
                name);

            await using (var empty = new MemoryStream())
            {
                await storage.PutObjectAsync($"{prefix}{name}{SignedSuffix}", empty, "application/octet-stream", cancellationToken);
            }

            await MarkSeenAsync(prefix, name, cancellationToken);
            return null;
        }

        var currentName = name;
        var ran = new List<string>();

        foreach (var processor in processors)
        {
            try
            {
                if (await processor.TryProcessAsync(
                        new InboxIngestContext(tenantId, userId, prefix, currentName, bytes), cancellationToken)
                    is not { } processed)
                {
                    continue; // declined: not enabled, or not the kind of file it acts on
                }

                bytes = processed.Bytes;
                currentName = await ReplaceAsync(prefix, currentName, processed, cancellationToken);
                ran.Add(processor.Name);
            }
            catch (Exception e)
            {
                // One processor failing must not deprive the item of the others, and must not leave it
                // unmarked — an item that throws every pass would otherwise be retried by the sweep forever.
                logger.LogWarning(e, "Inbox ingest processor {Processor} failed for {Item}; continuing.", processor.Name, currentName);
            }
        }

        await MarkAsync(prefix, currentName, ran, cancellationToken);
        return currentName;
    }

    // Replaces the item in place, or under a new name when the format changed — and takes the staged mask
    // sidecar with it, since the draft belongs to the item and not to its extension.
    private async Task<string> ReplaceAsync(
        string prefix,
        string name,
        InboxProcessed processed,
        CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var newName = stem + processed.Extension;

        await using (var payload = new MemoryStream(processed.Bytes))
        {
            await storage.PutObjectAsync(prefix + newName, payload, processed.ContentType, cancellationToken);
        }

        if (!string.Equals(newName, name, StringComparison.Ordinal))
        {
            var draft = $"{prefix}{name}{MaskSidecarSuffix}";
            if (await storage.ExistsAsync(draft, cancellationToken))
            {
                await storage.CopyObjectAsync(draft, $"{prefix}{newName}{MaskSidecarSuffix}", cancellationToken);
                await storage.DeleteObjectAsync(draft, cancellationToken);
            }

            await storage.DeleteObjectAsync(prefix + name, cancellationToken);
        }

        return newName;
    }

    /// <summary>
    /// Marks an item as seen without running anything — how the sweep grandfathers items that predate the
    /// feature, so shipping it does not silently rewrite an inbox full of files people already have.
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

/// <summary>One step of the inbox ingest pipeline (#494).</summary>
/// <remarks>
/// A processor returns null to decline — not enabled for this user, or not the kind of file it acts on — which
/// is the normal case and not a failure. When it does act it returns the new bytes AND the extension they
/// should carry, because a processor is allowed to change the format and the pipeline has to know: deskew
/// always emits PDF, while patch splitting keeps what it was given, and one must not silently undo what the
/// other preserved.
/// </remarks>
public interface IInboxIngestProcessor
{
    /// <summary>Recorded in the marker, so what happened to an item is answerable later.</summary>
    string Name { get; }

    Task<InboxProcessed?> TryProcessAsync(InboxIngestContext context, CancellationToken cancellationToken);
}

public sealed record InboxIngestContext(Guid TenantId, Guid UserId, string Prefix, string Name, byte[] Bytes);

public sealed record InboxProcessed(byte[] Bytes, string Extension, string ContentType);

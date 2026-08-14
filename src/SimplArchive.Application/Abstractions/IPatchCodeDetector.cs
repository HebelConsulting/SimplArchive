namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Finds the <b>Patch 3 separator sheets</b> in a batch scan — the printed sheets a person drops between
/// documents so a stack of paper can be fed in one go and come out as several documents (issue #492).
/// </summary>
/// <remarks>
/// <para>
/// Detection only: the caller does the cutting, because it already owns page algebra that gives the same
/// answer for both formats, and a detector that also cut would have to reproduce it.
/// </para>
/// <para>
/// <b>It runs in the OCR sidecar, and it has to.</b> Reading a patch code means reading pixels, and the Api
/// image is Alpine (musl) with no PDF rasteriser — the same constraint that put the thumbnail route there.
/// Doing TIFF in-process (NetVips can) and PDF in the sidecar would have meant two implementations of one
/// detector, drifting apart at exactly the tolerances that make detection work.
/// </para>
/// <para>
/// Null means <b>detection did not run</b> — no sidecar configured, or it failed — which is different from an
/// empty list, the ordinary answer for a batch nobody put separators in. The caller must not treat the two
/// alike: one leaves the scan as it is, the other is a batch that genuinely has nothing to cut.
/// </para>
/// </remarks>
public interface IPatchCodeDetector
{
    /// <summary>The separator pages, as 1-based page numbers in ascending order.</summary>
    /// <param name="kind">
    /// Which of the two paged formats these bytes are. Deliberately the same enum the OCR path uses rather
    /// than a twin of it — it names the sidecar's source formats, and there is one sidecar.
    /// </param>
    Task<IReadOnlyList<int>?> DetectSeparatorPagesAsync(
        byte[] bytes,
        SearchablePdfSourceKind kind,
        CancellationToken cancellationToken = default);
}

using System.Net.Http.Json;

namespace SimplArchive.Client.Services;

/// <summary>Starting an upload into the caller's inbox: one presigned PUT per file.</summary>
/// <remarks>
/// Extracted at the SECOND caller, which is where CLAUDE.md says to stop copying. The Inbox tab has staged
/// uploads this way since the tab existed; dropping files onto the Personal ▸ Inbox tree node (#467) is the
/// second, and it lives in a different component with its own <c>DotNetObjectReference</c> — so without this the
/// same POST would have been written twice, in two files, with two error-handling stories.
/// </remarks>
public sealed class InboxUploads(HttpClient http, ApiRoot apiRoot)
{
    // The `processed` address of each file whose PUT is in flight, in upload order. Held here rather than in
    // the two components for the same reason the POST is: one implementation of the protocol, or the second
    // caller gets a subtly different one.
    private readonly List<string> _pendingProcessed = [];

    /// <summary>
    /// A presigned PUT for one file, or <c>null</c> when the inbox refused it. Null rather than an exception
    /// because the caller is a JS-invoked upload loop that reports per-file failures and continues with the
    /// rest — one bad file must not abandon the others.
    /// </summary>
    public async Task<string?> CreateTargetAsync(string fileName)
    {
        try
        {
            var response = await http.PostAsJsonAsync(await apiRoot.RequireAsync("inbox"), new { fileName });
            var target = await response.Content.ReadFromJsonAsync<UploadInboxResponse>();

            // Remember where to signal completion. A server that does not advertise the rel simply gets no
            // signal — the sweep worker still catches the file (ADR 0543: a missing rel means "not available
            // here", never a composed URL).
            if (target?.Href("processed") is { } processed)
            {
                _pendingProcessed.Add(processed);
            }

            return target?.UploadUrl;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells the server each uploaded file has arrived, so the ingest pipeline runs NOW.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "client-signalled path" <c>InboxIngestSweepWorker</c>'s own comment describes — and it was
    /// never wired, because the rel that reaches it was not advertised. The endpoint existed and worked, so
    /// nothing failed; uploads simply waited up to the sweep's five-minute poll for their deskew and their
    /// patch-code cut, which read as "the split documents do not show up" and "the crooked page was not
    /// straightened". One missing link, two bug reports.
    /// </para>
    /// <para>
    /// Deliberately awaited rather than fired and forgotten: the pipeline runs synchronously and returns the
    /// resulting names, so the caller can reload once and show the split items. Failures are swallowed per
    /// file for the same reason the target POST is — the sweep remains the safety net, and one bad file must
    /// not abandon the rest.
    /// </para>
    /// </remarks>
    public async Task SignalProcessedAsync()
    {
        foreach (var href in _pendingProcessed)
        {
            try
            {
                await http.PostAsJsonAsync(href, new { });
            }
            catch (Exception)
            {
                // The sweep worker will pick it up.
            }
        }

        _pendingProcessed.Clear();
    }

    public sealed record UploadInboxResponse
    {
        public string UploadUrl { get; set; } = "";

        public List<SimplArchive.Client.Hypermedia.LinkResponse> Links { get; set; } = [];

        public string? Href(string rel) => SimplArchive.Client.Hypermedia.Links.Href(Links, rel);
    }
}

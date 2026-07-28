using System.Threading.Channels;

namespace SimplArchive.Infrastructure.Search;

// Coordinates the background search-index rebuild (ADR 0139): the admin endpoint requests a rebuild and the
// hosted SearchReindexService consumes it. The bounded channel coalesces concurrent requests into one, and
// the fields expose status for the endpoint to report. Registered always (the endpoint depends on it) even
// when OpenSearch isn't configured — where nothing consumes the requests.
public sealed class SearchReindexState
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<bool> Requests => _channel.Reader;

    // Enqueue a rebuild (coalesced if one is already pending). Returns whether it was newly enqueued.
    public bool Request() => _channel.Writer.TryWrite(true);

    public volatile bool IsRunning;

    // Documents indexed by the last completed rebuild; -1 until the first one finishes.
    public int LastIndexedCount = -1;
}

namespace SimplArchive.Client.Services;

/// <summary>Formats a byte count for display next to a file.</summary>
/// <remarks>
/// One implementation, because there were two and they had already drifted: the workbench page carried a
/// nullable, GB-aware <c>FormatSize</c> for document rows and a second <c>long</c> overload that stopped at MB,
/// so a staged intray file over a gigabyte rendered as "2048 MB". Nobody would ever have found that by reading
/// either copy — it is only visible with both side by side, which is the whole argument for not making a third
/// (CLAUDE.md, "the same work across several types is ONE implementation").
/// </remarks>
public static class ByteSize
{
    /// <summary>Renders <paramref name="bytes"/> as B/KB/MB/GB; an empty string when it is unknown.</summary>
    public static string Format(long? bytes) => bytes switch
    {
        null => "",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };
}

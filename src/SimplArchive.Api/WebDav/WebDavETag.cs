using System.Globalization;

namespace SimplArchive.Api.WebDav;

/// <summary>
/// The entity tag a WebDAV client uses to confirm its own write (RFC 4918 §8.6, RFC 9110 §8.8.3).
/// </summary>
/// <remarks>
/// <para>
/// This gateway emitted no ETag at all — not the <c>getetag</c> property, not the header — and the cost was
/// measured on the wire (#794). Immediately after writing a document, a word processor asks for exactly two
/// properties:
/// </para>
/// <code>
/// &lt;D:propfind xmlns:D="DAV:"&gt;&lt;D:prop&gt;&lt;D:getlastmodified/&gt;&lt;D:getetag/&gt;&lt;/D:prop&gt;&lt;/D:propfind&gt;
/// </code>
/// <para>
/// It got a <c>207</c> whose propstat said <c>200 OK</c> and simply did not mention <c>getetag</c> — so it could
/// not confirm the write had landed, and it retried the whole save. Four times, in six seconds, before rolling
/// back over its own good file. <b>The status line said yes and the body said nothing</b>, which is the same
/// class of defect as the frozen timestamp: a healthy-looking answer that a client cannot use.
/// </para>
/// <para>
/// <b>Derived from size and modification time</b>, not stored, so every path can produce it from what it already
/// has — the tree from its version, an Intray item and a safe-save staging file from their object metadata —
/// with no extra round trip and no risk of two code paths disagreeing about one resource. A strong tag, because
/// it changes whenever the bytes do. Two writes of identical length within the same second collide; that is
/// acceptable here, since what a client needs is that the tag CHANGES when the content does and stays stable
/// between its write and its read-back.
/// </para>
/// </remarks>
internal static class WebDavETag
{
    /// <summary>The quoted entity tag for a resource of this size, last modified at this instant.</summary>
    /// <param name="contentTag">
    /// The store's own tag for these bytes, when there is one. Preferred over the size/time pair because it is
    /// a CONTENT validator: the pair cannot separate two writes of the same length inside one second, and an
    /// object store dates objects to the second. That is not a theoretical window — a document edited twice by
    /// an autosave lands both writes in it, and a tag that does not move is worse than an absent one, because
    /// it is a positive claim that the file is unchanged (#794). It was caught by a test that passed alone and
    /// failed in the suite: run fast enough, both saves shared a second.
    /// </param>
    internal static string For(long size, DateTimeOffset modified, string? contentTag = null) =>
        string.IsNullOrWhiteSpace(contentTag)
            ? $"\"{modified.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}-{size.ToString(CultureInfo.InvariantCulture)}\""
            : $"\"{contentTag.Trim('"')}\"";
}

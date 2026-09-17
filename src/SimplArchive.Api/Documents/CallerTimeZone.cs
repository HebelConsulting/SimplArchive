using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.Api.Documents;

/// <summary>
/// Which zone the caller's DATE filters are expressed in — their stored preference, else the zone their device
/// reported, else none (ADR 0801).
/// </summary>
/// <remarks>
/// <para>
/// <b>One answer, because two callers asking the same question two ways is how they come to disagree.</b> This
/// began as a private method on <c>SearchController</c>, and the export path — which asks the identical
/// question about the identical columns — simply never asked it, so a document a user could see dated the 16th
/// was excluded from an export filtered to the 16th (#1256). A second copy would have drifted the moment one
/// of them gained a rule the other did not.
/// </para>
/// <para>
/// A named service rather than an extension method because it has dependencies of its own — the principal and
/// the database — which is the line CLAUDE.md draws. The REQUEST is passed in rather than injected as an
/// <c>IHttpContextAccessor</c>: both callers are controllers and already hold it, this app registers no such
/// accessor today, and an ambient current-request is a dependency that cannot be seen at the call site. What
/// is shared is the RULE — which source wins, what the header is called, and that null is an answer.
/// </para>
/// </remarks>
public interface ICallerTimeZone
{
    /// <summary>The caller's zone id, or <c>null</c> to read their dates as UTC.</summary>
    Task<string?> ResolveAsync(HttpRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICallerTimeZone"/>
public sealed class CallerTimeZoneResolver(
    ICurrentUserAccessor currentUser,
    SimplArchiveDbContext dbContext) : ICallerTimeZone
{
    /// <summary>The header a client sets once, for its whole lifetime, to say where its device is.</summary>
    public const string HeaderName = "X-Time-Zone";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The stored preference WINS over the header.</b> It is the explicit override a user chose, and a
    /// device in another zone must not silently undo it — travelling should not change which documents an
    /// export contains.
    /// </para>
    /// <para>
    /// <b>Null is a real answer, not a failure.</b> A service account has no preference, and a scripted caller
    /// sends no header; both then get the behaviour that existed before any of this, which is what keeps them
    /// working unchanged. A zone is never inferred from the SERVER, because the server's zone is an accident of
    /// deployment and answering with it would make every caller inherit the container's.
    /// </para>
    /// </remarks>
    public async Task<string?> ResolveAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is { } userId)
        {
            var stored = await dbContext.Users
                .Where(user => user.Id == userId)
                .Select(user => user.DisplayTimeZoneId)
                .FirstOrDefaultAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }
        }

        return request.Headers.TryGetValue(HeaderName, out var header) && header.Count > 0 && !string.IsNullOrWhiteSpace(header[0])
            ? header[0]
            : null;
    }
}

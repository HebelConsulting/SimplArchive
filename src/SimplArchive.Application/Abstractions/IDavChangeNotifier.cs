namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Told after a commit that a DAV collection changed, so WebDAV-Push subscribers hear about writes that did
/// NOT arrive over DAV (#806) — the workbench, an import, the seeder. The DAV write path notifies for itself;
/// this is the same doorbell rung from the one place every other write passes through.
/// </summary>
/// <remarks>Optional, like the realtime notifier beside it: absent (tests, tools) means no doorbell, never a
/// failure — the change log rows are committed either way, and a poll still finds them.</remarks>
public interface IDavChangeNotifier
{
    /// <summary>Best-effort, post-commit: a push failure must never break the mutation.</summary>
    Task NotifyAsync(Guid folderId, long sequence, CancellationToken cancellationToken);
}

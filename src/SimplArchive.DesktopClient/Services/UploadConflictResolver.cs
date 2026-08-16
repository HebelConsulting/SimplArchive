using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Decides what to do when a dropped file's name is already used in the target folder.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the create returned 409, the status line showed a message the user had usually stopped
/// looking at, and the file was dropped — so a drag-and-drop appeared to do nothing at all. The two things they
/// could plausibly have meant are offered instead: a new version of the document already there, or a new document
/// under a different name, either way with the filing comment they would otherwise add afterwards.
/// </para>
/// <para>
/// Its own type rather than more methods on <c>MainWindowViewModel</c>, which is the largest entry on the
/// 1000-line debt list (#466) — the same reason <see cref="DropFiling"/> exists. It reports through callbacks and
/// prompts through one, so the caller keeps the status line and the view keeps the window: a service that opened
/// its own dialog could not be exercised without a display.
/// </para>
/// <para>
/// Deliberately named for its web counterpart (<c>SimplArchive.Client.Services.UploadConflictResolver</c>) — the
/// two clients are one surface for this feature (ADR 0511), and a shared name is what makes a divergence between
/// them visible in review rather than something to be discovered later.
/// </para>
/// </remarks>
public sealed class UploadConflictResolver(SimplArchiveApiClient api)
{
    /// <summary>What the user is being asked about: the file that collided, and a free name to offer instead.</summary>
    /// <param name="canFileAsVersion">False when the name is held by a FOLDER — see <see cref="ResolveAsync"/>.</param>
    public sealed record NameConflictRequest(string FileName, string SuggestedName, bool CanFileAsVersion);

    /// <summary>
    /// <c>version</c> = file it as a new version of the document already there; <c>rename</c> = file it as a new
    /// document called <see cref="NewName"/>. <see cref="Comment"/> is the version comment either way.
    /// </summary>
    public sealed record NameConflictChoice(string Action, string NewName, string Comment);

    /// <summary>Asks what the user meant and carries it out; true when the file was filed.</summary>
    /// <param name="childrenHref">The target folder's children address, which the caller already holds — used to
    /// read the siblings and, for the rename choice, to create the new document. Passed in so this class composes
    /// nothing (ADR 0543) and costs no extra fetch (ADR 0557).</param>
    public async Task<bool> ResolveAsync(
        string childrenHref,
        string fileName,
        byte[] bytes,
        Func<NameConflictRequest, Task<NameConflictChoice?>> prompt,
        Action<string> report,
        CancellationToken cancellationToken = default)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var conflict = await api.Documents.DescribeNameConflictAsync(childrenHref, stem, cancellationToken);

        // Sibling names are unique across folders AND documents, so the name can be held by a FOLDER. Adding a
        // version to one would turn it into a document, so that choice is only offered against a real document.
        var canFileAsVersion = conflict.Existing is { HasVersions: true };
        var choice = await prompt(new NameConflictRequest(fileName, conflict.SuggestedName, canFileAsVersion));
        if (choice is null)
        {
            return false;
        }

        return choice.Action == "version"
            ? await AsNewVersionAsync(conflict.Existing, fileName, extension, bytes, choice.Comment, report, cancellationToken)
            : await AsNewDocumentAsync(childrenHref, choice.NewName + extension, bytes, choice.Comment, report, cancellationToken);
    }

    private async Task<bool> AsNewVersionAsync(
        Node? existing,
        string fileName,
        string extension,
        byte[] bytes,
        string comment,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        // The row went away between the 409 and the listing. Rare, and refusing beats guessing which document
        // the user meant.
        if (existing is null)
        {
            report(string.Format(Strings.Get("StUploadNameTaken"), fileName));
            return false;
        }

        // The row carries the address a new version is posted to (ADRs 0555/0557) — no second lookup, and
        // nothing rebuilt from an id. A missing `versions` rel means it is not available here (ADR 0543).
        if (existing.Links is null || !existing.Links.TryGetValue("versions", out var versionsHref))
        {
            report(string.Format(Strings.Get("StUploadNoPermission"), fileName));
            return false;
        }

        await api.Documents.UploadNewVersionAsync(versionsHref, bytes, extension, comment, cancellationToken);
        return true;
    }

    private async Task<bool> AsNewDocumentAsync(
        string childrenHref,
        string fileName,
        byte[] bytes,
        string comment,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        try
        {
            await api.Documents.UploadFileAsync(childrenHref, fileName, bytes, comment, cancellationToken);
            return true;
        }
        catch (DocumentNameTakenException)
        {
            // The suggested name was free when it was offered and is not now, or the user typed a taken one.
            // Reported rather than re-prompting: a loop of dialogs is worse than one clear refusal.
            report(string.Format(Strings.Get("StUploadNameTaken"), fileName));
            return false;
        }
    }
}

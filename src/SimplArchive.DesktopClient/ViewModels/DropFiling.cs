using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// What a drop onto the Personal ▸ Inbox or Personal ▸ Check-out tree launcher does (#467): copy a document in
/// as a template, or bring an edited working copy back.
/// </summary>
/// <remarks>
/// <para>
/// Its own type rather than two more methods on <see cref="MainWindowViewModel"/>, which is 6,758 lines and the
/// largest entry on the 1000-line debt list (#466). CLAUDE.md treats adding to an over-limit class as needing
/// the same justification as creating one, so the view-model forwards and the work lives here.
/// </para>
/// <para>
/// It reports through callbacks rather than holding view-model state: the caller owns the status line and knows
/// what to refresh afterwards, and a type that reached back into the view-model would be the view-model again
/// with an extra file.
/// </para>
/// </remarks>
public sealed class DropFiling(SimplArchiveApiClient api)
{
    /// <summary>
    /// Copies documents into the caller's inbox as templates, carrying each one's mask and index values.
    /// </summary>
    /// <remarks>
    /// The copy is server-side — one request per document, no bytes through the client — and nothing is created
    /// in the archive: the copy becomes a document only if the user files it.
    /// </remarks>
    public async Task<int> CopyToInboxAsync(IReadOnlyList<Guid> documentIds, Action<string> report)
    {
        var copied = 0;
        foreach (var id in documentIds)
        {
            try
            {
                await api.CopyDocumentToInboxAsync(id);
                copied++;
            }
            catch (Exception)
            {
                // Per document, because a set can legitimately be mixed: a folder has no version to copy, and an
                // item whose name is already staged conflicts. One refusal must not abandon the rest.
                report(string.Format(Strings.Get("StTemplateFailedOne"), id));
            }
        }

        report(copied > 0
            ? string.Format(Strings.Get("StTemplateCopiedN"), copied)
            : Strings.Get("StTemplateNoneCopied"));
        return copied;
    }

    /// <summary>
    /// Puts edited files back as working copies, matched to checked-out documents BY NAME.
    /// </summary>
    /// <remarks>
    /// The round trip is download → edit offline → drag back, so the filename is what says which document a file
    /// belongs to; the launcher node carries none. A file naming nothing checked out is reported rather than
    /// ignored — silence is indistinguishable from a broken feature.
    /// </remarks>
    public async Task<int> StashAsync(
        IReadOnlyList<(string Name, byte[] Bytes)> files,
        IReadOnlyList<SimplArchiveApiClient.CheckoutItem> checkouts,
        Action<string> report)
    {
        var stashed = 0;
        foreach (var (name, bytes) in files)
        {
            var match = checkouts.FirstOrDefault(c =>
                string.Equals(c.Name + c.FileExtension, name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                report(string.Format(Strings.Get("StStashNotCheckedOut"), name));
                continue;
            }

            try
            {
                await api.SaveWorkingCopyAsync(match, bytes);
                stashed++;
            }
            catch (Exception ex)
            {
                report(string.Format(Strings.Get("StErrUpload2b"), name, ex.Message));
            }
        }

        if (stashed > 0)
        {
            report(string.Format(Strings.Get("StStashUploaded"), stashed));
        }

        return stashed;
    }
}

using System;
using System.IO;
using System.Threading.Tasks;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.Services;

/// <summary>
/// Stages a checked-out document's two sides to temp files and opens them in Beyond Compare.
/// </summary>
/// <remarks>
/// <para>
/// One implementation rather than two: the Compare dialog has offered this since ADR 0517, and the Check-out
/// row now offers it directly. Copying the staging would be the usual way a fix reaches one of them and not the
/// other — the temp-file naming, the left/right order, and the not-installed branch all have to agree, and
/// nothing would point out that they had stopped agreeing.
/// </para>
/// <para>
/// The button is shown whether or not the tool is installed (ADR 0518): a user who has never heard of the
/// integration cannot go looking for a hidden button, so a missing install leads to the vendor rather than
/// nowhere. That is a deliberate exception to ADR 0554, and it lives here so both callers inherit it.
/// </para>
/// </remarks>
public static class CheckoutDiffLauncher
{
    /// <summary>Opens the comparison; returns a status message, or an empty string on success.</summary>
    public static async Task<string> OpenAsync(
        SimplArchiveApiClient api, Guid documentId, string fileExtension, string? stashDownloadUrl)
    {
        if (!BeyondCompare.IsInstalled)
        {
            SystemBrowser.Open("https://www.scootersoftware.com");
            return string.Empty;
        }

        if (stashDownloadUrl is null)
        {
            // No working copy staged means there is no right-hand side to compare against — the row should not
            // have offered this, so say so rather than opening the tool on one file.
            return Strings.Get("StCompareUnavailable");
        }

        try
        {
            // Left: the current confirmed version (what the server diffs against). Right: the working-copy stash.
            var current = await StageAsync(await api.DownloadCurrentVersionAsync(documentId), "current", fileExtension);
            var working = await StageAsync(await api.Checkout.DownloadStashAsync(stashDownloadUrl), "working", fileExtension);
            return BeyondCompare.Launch(current, working) ? string.Empty : Strings.Get("StErrBeyondCompareLaunch");
        }
        catch (Exception e)
        {
            return string.Format(Strings.Get("StErrBeyondCompare"), e.Message);
        }
    }

    private static async Task<string> StageAsync(byte[] bytes, string label, string fileExtension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"simplarchive-{label}-{Guid.NewGuid():N}{fileExtension}");
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }
}

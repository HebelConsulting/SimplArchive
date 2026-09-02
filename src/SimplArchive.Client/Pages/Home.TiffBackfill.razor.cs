using System.Net;
using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// The tenant-admin searchable-PDF backfill (ADR 0274): count what is still a plain TIFF, confirm, enqueue.
//
// It is here rather than in the shell because it is not shell coordination at all -- it is a one-off
// MAINTENANCE action that happens to be reachable from the ribbon. It touches no selection, no tree and no
// pane; it asks the API a question, shows a number, and enqueues work. The shell's remaining lifecycle members
// next to which it used to sit share none of that.
//
// It came out of the "Annotations" tombstone -- a heading left behind when ADR 0558 moved annotation authoring
// to AnnotationEditor, under which unrelated members then accumulated for months (#941). What is left there
// now is genuine shell work: the selection, the auth cascade, the tab switch, and the caller's rights, which
// the markup gates on.
public partial class Home
{
    private record BackfillResponse { public int Count { get; set; } }

    // Tenant-admin ribbon action (ADR 0274): convert every existing "current TIFF" document to a searchable
    // PDF — shows the pending count, confirms, then enqueues.
    private async Task RunTiffBackfillAsync()
    {
        int pending;
        try
        {
            pending = (await Http.GetFromJsonAsync<BackfillResponse>(await ApiRoot.RequireAsync("searchablePdfBackfill")))?.Count ?? 0;
        }
        catch (Exception)
        {
            Snackbar.Add(Strings.Get("StErrCheckConversions"), Severity.Error);
            return;
        }

        if (pending == 0)
        {
            Snackbar.Add(Strings.Get("StNoConversionNeeded"), Severity.Info);
            return;
        }

        var confirmed = await DialogService.ShowMessageBoxAsync(new MessageBoxOptions
        {
            Title = "Convert scanned documents",
            Message = $"Queue {pending} scanned document(s) (TIFFs + scanned PDFs) for searchable-PDF conversion?",
            YesText = "Convert",
            CancelText = "Cancel",
        });
        if (confirmed != true)
        {
            return;
        }

        try
        {
            var resp = await Http.PostAsync(await ApiRoot.RequireAsync("searchablePdfBackfill"), null);
            if (resp.IsSuccessStatusCode)
            {
                var count = (await resp.Content.ReadFromJsonAsync<BackfillResponse>())?.Count ?? 0;
                Snackbar.Add(string.Format(Strings.Get("StQueuedConversion"), count), Severity.Success);
            }
            else
            {
                Snackbar.Add(Strings.Get(resp.StatusCode == HttpStatusCode.Forbidden ? "NoPermission" : "StErrStartConversionPlain"), Severity.Warning);
            }
        }
        catch (Exception)
        {
            Snackbar.Add(Strings.Get("StErrConversionStart"), Severity.Error);
        }
    }
}

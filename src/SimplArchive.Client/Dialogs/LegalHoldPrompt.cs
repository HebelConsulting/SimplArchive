using MudBlazor;
using SimplArchive.Localization;

namespace SimplArchive.Client.Dialogs;

/// <summary>
/// Asks for a new matter's name and reason.
/// </summary>
/// <remarks>
/// One implementation with two callers — the Legal holds tab's "new hold" and the contents-pane row action
/// that puts a single document under a new matter. Written once rather than twice, because two copies of a
/// prompt are two places for its title, its parameters and its cancel semantics to drift apart.
/// </remarks>
public static class LegalHoldPrompt
{
    public static async Task<LegalHoldDialog.LegalHoldFormResult?> ShowAsync(
        IDialogService dialogService, string? suggestedName = null)
    {
        var parameters = new DialogParameters<LegalHoldDialog> { { x => x.SuggestedName, suggestedName } };
        var dialog = await dialogService.ShowAsync<LegalHoldDialog>(Strings.Get("LhDlgTitle"), parameters);
        var result = await dialog.Result;
        return result is { Canceled: false, Data: LegalHoldDialog.LegalHoldFormResult r } ? r : null;
    }
}

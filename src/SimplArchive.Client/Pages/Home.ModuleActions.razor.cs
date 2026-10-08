using System.Net.Http.Json;
using MudBlazor;
using SimplArchive.Client.Dialogs;
using SimplArchive.Client.Models;
using SimplArchive.Localization;

namespace SimplArchive.Client.Pages;

// A module's pick-then-act actions on the web (#1628), matching the desktop (ADR 0511): the picker commits, the
// module's sentence shows on success, and a value revealed once (ABI 1.8) shows in the copy-once dialog and is kept
// nowhere else. Its own partial because the shell's other files are near the 1000-line limit.
public partial class Home
{
    private async Task RunModuleActionAsync(ModuleActionDto action)
    {
        var parameters = new DialogParameters { ["Action"] = action };
        var dialog = await DialogService.ShowAsync<ModuleActionDialog>(action.Label, parameters,
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });
        if (await dialog.Result is not { Canceled: false, Data: ModuleActionDialog.Outcome outcome })
        {
            return;
        }

        Snackbar.Add(outcome.Message ?? string.Format(Strings.Get("ModuleActionDone"), action.Label), Severity.Success);

        if (outcome.RevealValue is { } revealed)
        {
            var reveal = new DialogParameters<GeneratedPasswordDialog>
            {
                { d => d.Password, revealed },
                { d => d.Intro, Strings.Get("RevealOnceHint") },
            };
            await (await DialogService.ShowAsync<GeneratedPasswordDialog>(outcome.RevealLabel ?? action.Label, reveal)).Result;
        }

        // The action changed its subject, so the actions on offer did too (ADR 0550): a credential issued is not
        // offered again. Re-read the document the action was addressed to.
        if (Detail.Links is { } links && links.TryGetValue("self", out var selfHref))
        {
            var document = await Http.GetFromJsonAsync<DocumentDetailResponse>(selfHref.TrimStart('/'));
            Detail.ModuleActions = document?.ModuleActions ?? [];
            StateHasChanged();
        }
    }
}

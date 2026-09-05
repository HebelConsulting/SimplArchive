using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The generic action surface (ADR 0743): a link the server labeled renders as a button in the detail pane
// with no client knowledge of the rel — how a module's actions (accept-aircraft, submit, …) reach this
// client without its code ever naming them. The label is server-rendered and localized; the rel's presence
// is the affordance (ADR 0543), so there is nothing to disable — an unavailable action simply is not here.
public partial class MainWindowViewModel
{
    /// <summary>The selected document's labeled actions, rebuilt on every detail load.</summary>
    public ObservableCollection<DocumentsClient.GenericActionInfo> DetailGenericActions { get; } = [];

    public bool HasDetailGenericActions => DetailGenericActions.Count > 0;

    // Rebuild rather than mutate, and CLEARED when the subject changes (ADR 0559): an action inherited from
    // the previously selected document would execute against the wrong subject.
    internal void SetDetailGenericActions(IReadOnlyList<DocumentsClient.GenericActionInfo>? actions)
    {
        DetailGenericActions.Clear();
        foreach (var action in actions ?? [])
        {
            DetailGenericActions.Add(action);
        }

        OnPropertyChanged(nameof(HasDetailGenericActions));
    }

    [RelayCommand]
    private async Task ExecuteGenericAction(DocumentsClient.GenericActionInfo? action)
    {
        if (action is null || _api is null)
        {
            return;
        }

        try
        {
            await _api.Documents.ExecuteActionAsync(action);
            Status = action.Label;

            // The action changed the subject's state, so its rels — including this surface — are stale;
            // re-reading the resource is what makes a state transition's NEW actions appear (ADR 0550).
            if (_detailLinks is { } links && links.TryGetValue("self", out var selfHref))
            {
                var detail = await _api.Documents.GetDocumentDetailAsync(selfHref);
                SetDetailGenericActions(detail.GenericActions);
            }
        }
        catch (ApiActionException e)
        {
            // The problem detail is the explanation (ADR 0742: a diagnosis, not a verdict).
            ReportError(e.Message);
        }
        catch (Exception e)
        {
            ReportError(e.Message);
        }
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// One choice in a module action's picker — what it is called, and why it qualifies.
/// </summary>
/// <remarks>
/// The detail line is the module's own sentence ("FI(A) · certificate valid to … · offered and free"), shown
/// verbatim. A picker whose entries cannot be told apart is one nobody can use with confidence, and only the
/// module knows what distinguishes its candidates.
/// </remarks>
public sealed class ModuleActionOptionViewModel
{
    public required string Value { get; init; }

    public required string Label { get; init; }

    public string? Detail { get; init; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// The picker behind a module's pick-then-act surface (core ADR 0786).
/// </summary>
/// <remarks>
/// Deliberately knows no module and no rel: the prompt and the choices arrive from the server, and the
/// chosen value goes back to the href the action named. That is what lets a vertical add an action to this
/// client without a line of code here (ADR 0737) — and what stops this dialog becoming the place every
/// future module's special case accumulates.
/// </remarks>
public sealed partial class ModuleActionPickerViewModel : ObservableObject
{
    public ModuleActionPickerViewModel(DocumentsClient.ModuleActionInfo action,
        IReadOnlyList<DocumentsClient.ModuleActionOption> options)
    {
        Action = action;
        foreach (var option in options)
        {
            Options.Add(new ModuleActionOptionViewModel
            {
                Value = option.Value,
                Label = option.Label,
                Detail = option.Detail,
            });
        }

        Selected = Options.FirstOrDefault();
    }

    public DocumentsClient.ModuleActionInfo Action { get; }

    public string Prompt => Action.Prompt;

    public string Title => Action.Label;

    public ObservableCollection<ModuleActionOptionViewModel> Options { get; } = [];

    [ObservableProperty] private ModuleActionOptionViewModel? _selected;

    /// <summary>Nothing to choose from is a state the dialog must SAY, not one it can hide.</summary>
    public bool HasOptions => Options.Count > 0;

    public bool HasNoOptions => Options.Count == 0;

    /// <summary>The chosen value, or null when the dialog was dismissed without one.</summary>
    public string? Result { get; set; }
}

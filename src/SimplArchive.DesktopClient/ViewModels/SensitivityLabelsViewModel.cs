using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The management dialog VM for the tenant's configurable sensitivity labels (ADR "Configurable sensitivity labels
// + upload defaults") — list + create + edit (rename/recolour/rank/watermark) + retire/un-retire via the real
// api client. Mirrors the desktop Tags admin.
public sealed partial class SensitivityLabelsViewModel : ObservableObject
{
    private readonly SimplArchiveApiClient _api;

    public SensitivityLabelsViewModel(SimplArchiveApiClient api) => _api = api;

    public ObservableCollection<SensitivityLabelRow> Labels { get; } = [];

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newColor = string.Empty;
    [ObservableProperty] private int _newRank = 1;
    [ObservableProperty] private bool _newWatermark;
    [ObservableProperty] private string _status = string.Empty;

    /// <summary>Whether the server said THIS caller may manage the label catalog (#873).</summary>
    /// <remarks>
    /// Defaults to false so a load that fails leaves the editor closed rather than open — absence is "not
    /// available to you, here, now" (ADR 0543), and an unanswered question is not a yes.
    /// </remarks>
    [ObservableProperty] private bool _canManage;

    public async Task LoadAsync()
    {
        Labels.Clear();
        try
        {
            // The catalog's `canManage` was PARSED and then dropped on the floor (#873): AdminClient reads it
            // into SensitivityLabelCatalog and this loop took only `.Items`, so Add / Save / Retire rendered for
            // a caller the server had already said cannot manage, and the refusal arrived as caught-exception
            // status text. The gate existed on the wire the whole time — this is the cheapest fix in the audit.
            var catalog = await _api.Admin.GetSensitivityLabelsAsync();
            CanManage = catalog.CanManage;

            foreach (var l in catalog.Items)
            {
                Labels.Add(new SensitivityLabelRow(l.Id, l.Name, l.Rank, l.Color, l.Watermark, l.Retired, l.SelfHref, l.RetireHref, l.UnretireHref));
            }
        }
        catch (Exception e) { Status = e.Message; }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            return;
        }

        try
        {
            await _api.Admin.CreateSensitivityLabelAsync(NewName.Trim(), NewRank, string.IsNullOrWhiteSpace(NewColor) ? null : NewColor.Trim(), NewWatermark);
            NewName = string.Empty; NewColor = string.Empty; NewWatermark = false;
            await LoadAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not add the label."; }
    }

    [RelayCommand]
    private async Task Save(SensitivityLabelRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            if (row.SelfHref is not { } selfHref) { return; }

            await _api.Admin.UpdateSensitivityLabelAsync(selfHref, row.Name.Trim(), row.Rank, string.IsNullOrWhiteSpace(row.Color) ? null : row.Color!.Trim(), row.Watermark);
            await LoadAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not update the label."; }
    }

    [RelayCommand]
    private async Task Retire(SensitivityLabelRow? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            // Which rel is present decides the transition — the server already answered "retire or un-retire?".
            if (row.UnretireHref is { } unretire) { await _api.Admin.UnretireSensitivityLabelAsync(unretire); }
            else if (row.RetireHref is { } retire) { await _api.Admin.RetireSensitivityLabelAsync(retire); }
            else { return; }
            await LoadAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not change the label."; }
    }
}

public sealed partial class SensitivityLabelRow : ObservableObject
{
    public SensitivityLabelRow(Guid id, string name, int rank, string? color, bool watermark, bool retired,
        string? selfHref, string? retireHref, string? unretireHref)
    {
        Id = id;
        _name = name;
        _rank = rank;
        _color = color;
        _watermark = watermark;
        Retired = retired;
        SelfHref = selfHref;
        RetireHref = retireHref;
        UnretireHref = unretireHref;
    }

    public Guid Id { get; }
    public bool Retired { get; }

    // The addresses the server advertised for this row (ADR 0543, issue #416) — the view model follows them
    // instead of rebuilding /sensitivity-labels/{id} three times over.
    public string? SelfHref { get; }
    public string? RetireHref { get; }
    public string? UnretireHref { get; }
    public string RetireLabel => Retired ? "Un-retire" : "Retire";

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _rank;
    [ObservableProperty] private string? _color;
    [ObservableProperty] private bool _watermark;
}

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

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newColor = "";
    [ObservableProperty] private int _newRank = 1;
    [ObservableProperty] private bool _newWatermark;
    [ObservableProperty] private string _status = "";

    public async Task LoadAsync()
    {
        Labels.Clear();
        try
        {
            foreach (var l in (await _api.GetSensitivityLabelsAsync()).Items)
            {
                Labels.Add(new SensitivityLabelRow(l.Id, l.Name, l.Rank, l.Color, l.Watermark, l.Retired));
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
            await _api.CreateSensitivityLabelAsync(NewName.Trim(), NewRank, string.IsNullOrWhiteSpace(NewColor) ? null : NewColor.Trim(), NewWatermark);
            NewName = ""; NewColor = ""; NewWatermark = false;
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
            await _api.UpdateSensitivityLabelAsync(row.Id, row.Name.Trim(), row.Rank, string.IsNullOrWhiteSpace(row.Color) ? null : row.Color!.Trim(), row.Watermark);
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
            if (row.Retired) { await _api.UnretireSensitivityLabelAsync(row.Id); } else { await _api.RetireSensitivityLabelAsync(row.Id); }
            await LoadAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not change the label."; }
    }
}

public sealed partial class SensitivityLabelRow : ObservableObject
{
    public SensitivityLabelRow(Guid id, string name, int rank, string? color, bool watermark, bool retired)
    {
        Id = id;
        _name = name;
        _rank = rank;
        _color = color;
        _watermark = watermark;
        Retired = retired;
    }

    public Guid Id { get; }
    public bool Retired { get; }
    public string RetireLabel => Retired ? "Un-retire" : "Retire";

    [ObservableProperty] private string _name;
    [ObservableProperty] private int _rank;
    [ObservableProperty] private string? _color;
    [ObservableProperty] private bool _watermark;
}

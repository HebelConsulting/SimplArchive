using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

/// <summary>
/// The Tag-catalog admin tab: the controlled vocabulary, and the four acts that maintain it (#941).
/// </summary>
/// <remarks>
/// <para>
/// <b>A partial rather than its own type</b>, by ADR 0733's rule as #941 states it: a cluster earns its own
/// type when it can stand alone, and stays a partial when it writes the shell's own state. These commands
/// report through the shell's status line and act through its api client, so they cannot.
/// </para>
/// <para>
/// <b>What this extraction is really about is the HEADING.</b> The banner this came from claimed 120 lines of
/// "Tag catalog admin"; the catalog is the 80 below, and the other 38 were <c>ClearDetail</c> and
/// <c>HasSelection</c> — which belong to the detail pane and the selection, and stayed behind. That is the
/// fourth lying banner #941 has turned up, and the cause is mechanical: inserting a member above a comment
/// moves neither, so in a long file a heading decays silently and the next person picks a "section" that is
/// really three. Read what a heading covers before trusting it — a member inventory takes thirty seconds and
/// is the difference between a cohesive extraction and a badly-named new class.
/// </para>
/// </remarks>
public partial class MainWindowViewModel
{
    public ObservableCollection<TagCatalogRow> TagCatalogAdmin { get; } = [];
    [ObservableProperty] private string _newTagName = string.Empty;
    [ObservableProperty] private string _newTagColor = string.Empty;

    private async Task LoadTagCatalogAsync()
    {
        if (_api is null)
        {
            return;
        }

        TagCatalogAdmin.Clear();
        try
        {
            foreach (var t in (await _api.Documents.GetTagCatalogWithColorsAsync()).Items)
            {
                TagCatalogAdmin.Add(new TagCatalogRow(t));
            }
        }
        catch (Exception) { /* not readable */ }
    }

    [RelayCommand]
    private async Task CreateTag()
    {
        if (_api is null || string.IsNullOrWhiteSpace(NewTagName))
        {
            return;
        }

        try
        {
            await _api.Documents.CreateTagAsync(NewTagName.Trim(), string.IsNullOrWhiteSpace(NewTagColor) ? null : NewTagColor.Trim());
            NewTagName = string.Empty;
            NewTagColor = string.Empty;
            await LoadTagCatalogAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not add the tag."; }
    }

    [RelayCommand]
    private async Task SaveTag(TagCatalogRow? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try
        {
            await _api.Documents.UpdateTagAsync(row.Source, row.Name.Trim(), string.IsNullOrWhiteSpace(row.Color) ? "" : row.Color!.Trim());
            await LoadTagCatalogAsync();
        }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not update the tag."; }
    }

    [RelayCommand]
    private async Task RetireTag(TagCatalogRow? row)
    {
        if (_api is null || row is null)
        {
            return;
        }

        try { await _api.Documents.RetireTagAsync(row.Source); await LoadTagCatalogAsync(); }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not retire the tag."; }
    }

    [RelayCommand]
    private async Task MergeTag(TagCatalogRow? row)
    {
        if (_api is null || row?.MergeTarget is not { } target || target.Id == row.Id)
        {
            return;
        }

        try { await _api.Documents.MergeTagAsync(row.Source, target.Id); await LoadTagCatalogAsync(); }
        catch (Exception e) { Status = e is ApiActionException a ? a.Message : "Could not merge the tags."; }
    }

}

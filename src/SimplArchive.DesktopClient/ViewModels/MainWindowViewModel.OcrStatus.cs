using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;
using SimplArchive.Localization;

namespace SimplArchive.DesktopClient.ViewModels;

// The OCR verdict line + Make searchable (#999): the persisted detector verdict rendered where the user who
// wonders why their scan is unsearchable is actually looking (ADR 0626's principle in the pane), and the
// override that lets them overrule it — the rel's presence is the affordance (ADR 0543).
public partial class MainWindowViewModel
{
    private string? _makeSearchableHref;

    /// <summary>The verdict line's localized text, empty when the version is unjudged.</summary>
    public string SysOcrVerdictText { get; private set; } = string.Empty;

    public bool HasOcrStatusLine => SysOcrVerdictText.Length > 0 || CanMakeSearchable;

    public bool CanMakeSearchable => _makeSearchableHref is not null;

    internal void SetOcrStatus(string? verdict, string? makeSearchableHref)
    {
        _makeSearchableHref = makeSearchableHref;
        SysOcrVerdictText = verdict switch
        {
            "ConvertibleScan" => Strings.Get("OcrVerdictConvertibleScan"),
            "NotAScan" => Strings.Get("OcrVerdictNotAScan"),
            "Unreadable" => Strings.Get("OcrVerdictUnreadable"),
            _ => string.Empty,
        };
        OnPropertyChanged(nameof(SysOcrVerdictText));
        OnPropertyChanged(nameof(HasOcrStatusLine));
        OnPropertyChanged(nameof(CanMakeSearchable));
    }

    [RelayCommand]
    private async Task MakeSearchable()
    {
        if (_api is null || _makeSearchableHref is not { } href)
        {
            return;
        }

        try
        {
            await _api.Documents.MakeSearchableAsync(href);
            Status = Strings.Get("MakeSearchableQueued");
        }
        catch (ApiActionException e)
        {
            Status = e.Message;
        }
        catch (Exception e)
        {
            Status = e.Message;
        }
    }
}

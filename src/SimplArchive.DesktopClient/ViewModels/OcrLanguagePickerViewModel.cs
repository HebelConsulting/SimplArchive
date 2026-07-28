using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// One language row in the OCR-languages picker (ADR "System fields + OCR-language mask field"). Priority 0 =
// not selected; >0 = its click order (1 = highest priority = first in the Tesseract "+"-joined string).
public sealed partial class OcrLanguageItemViewModel : ObservableObject
{
    public required string Code { get; init; }

    public required string DisplayName { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected))]
    [NotifyPropertyChangedFor(nameof(PriorityLabel))]
    private int _priority;

    public bool IsSelected => Priority > 0;

    public string PriorityLabel => Priority > 0 ? Priority.ToString() : "";
}

// The ordered multi-select picker: clicking a language appends it at the next priority; clicking a selected
// one removes it and renumbers the rest. OrderedCodes() returns the selection in priority order.
public sealed partial class OcrLanguagePickerViewModel : ObservableObject
{
    public ObservableCollection<OcrLanguageItemViewModel> Languages { get; } = [];

    public OcrLanguagePickerViewModel(IReadOnlyList<SimplArchiveApiClient.OcrLanguageOption> catalog, IReadOnlyList<string> selected)
    {
        var order = selected.ToList();
        foreach (var option in catalog)
        {
            var index = order.IndexOf(option.Code);
            Languages.Add(new OcrLanguageItemViewModel
            {
                Code = option.Code,
                DisplayName = option.DisplayName,
                Priority = index >= 0 ? index + 1 : 0,
            });
        }
    }

    [RelayCommand]
    private void Toggle(OcrLanguageItemViewModel item)
    {
        if (item.Priority > 0)
        {
            var removed = item.Priority;
            item.Priority = 0;
            foreach (var other in Languages)
            {
                if (other.Priority > removed)
                {
                    other.Priority--;
                }
            }
        }
        else
        {
            item.Priority = Languages.Count(l => l.Priority > 0) + 1;
        }
    }

    public List<string> OrderedCodes() =>
        Languages.Where(l => l.Priority > 0).OrderBy(l => l.Priority).Select(l => l.Code).ToList();
}

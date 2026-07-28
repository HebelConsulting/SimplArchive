using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A mask choice in the mask-change dropdown (ADR "Editable mask on the detail pane"). MaskId null = "(No mask)".
public sealed record MaskChoiceViewModel(Guid? MaskId, string Name)
{
    public override string ToString() => Name;
}

// One editable index field in the mask edit mode — renders a type-aware editor bound to the field's DataType
// (ADR "Editable mask on the detail pane"): a date picker for Date, a checkbox for Boolean, a multi-line box
// for MultiSelect (one value per line), else a single-line text box (Text / Number / SingleSelect).
public sealed partial class MaskFieldEditViewModel : ObservableObject
{
    public required Guid FieldDefinitionId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsRequired { get; init; }

    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private System.DateTimeOffset? _dateValue;
    [ObservableProperty] private bool _boolValue;

    public bool IsDate => DataType == "Date";
    public bool IsBoolean => DataType == "Boolean";
    public bool IsMultiSelect => DataType == "MultiSelect";
    public bool IsSingleLine => !IsDate && !IsBoolean && !IsMultiSelect;
    public string Label => IsRequired ? $"{Name} *" : Name;

    public static MaskFieldEditViewModel Create(SimplArchiveApiClient.MaskFieldInfo definition, IReadOnlyList<string> values)
    {
        var field = new MaskFieldEditViewModel
        {
            FieldDefinitionId = definition.Id,
            Name = definition.Name,
            DataType = definition.DataType,
            IsRequired = definition.IsRequired,
        };

        switch (definition.DataType)
        {
            case "Date":
                field.DateValue = values.Count > 0 && System.DateTimeOffset.TryParse(values[0], out var d)
                    ? new System.DateTimeOffset(d.Date, System.TimeSpan.Zero) : null;
                break;
            case "Boolean":
                field.BoolValue = values.Count > 0 && values[0].Equals("true", System.StringComparison.OrdinalIgnoreCase);
                break;
            case "MultiSelect":
                field.TextValue = string.Join("\n", values);
                break;
            default:
                field.TextValue = values.Count > 0 ? values[0] : "";
                break;
        }

        return field;
    }

    public IReadOnlyList<string> ToValues() => DataType switch
    {
        "Date" => DateValue is { } d ? [d.ToString("yyyy-MM-dd")] : [],
        "Boolean" => [BoolValue ? "true" : "false"],
        "MultiSelect" => TextValue.Split('\n').Select(v => v.Trim()).Where(v => v.Length > 0).ToList(),
        _ => string.IsNullOrWhiteSpace(TextValue) ? [] : [TextValue.Trim()],
    };
}

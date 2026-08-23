using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A mask choice in the mask-change dropdown (ADR "Editable mask on the detail pane"). MaskId null = "(No mask)".
// Mask is the catalogue row the server sent, carried so reading the mask's fields follows the address it
// advertised (ADR 0543/0555). Null for the "(No mask)" entry and for the designer-preview rows, which reach
// no server — every real, selectable mask has it.
public sealed record MaskChoiceViewModel(Guid? MaskId, string Name, SimplArchive.DesktopClient.Services.MasksClient.MaskOptionInfo? Mask = null)
{
    public override string ToString() => Name;
}

// One editable index field in the mask edit mode — renders a type-aware editor bound to the field's DataType
// (ADR "Editable mask on the detail pane"): a date picker for Date, a checkbox for Boolean, a multi-line box
// for a LIST (one value per line), else a single-line text box (Text / Number / SingleSelect / EmailAddress).
//
// The web client's EditField is the same shape and must stay it (ADR 0511) — one surface, two renderings.
public sealed partial class MaskFieldEditViewModel : ObservableObject
{
    public required Guid FieldDefinitionId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public bool IsRequired { get; init; }

    /// <summary>Whether this field holds many values (#703).</summary>
    public bool IsList { get; init; }

    [ObservableProperty] private string _textValue = "";
    [ObservableProperty] private System.DateTimeOffset? _dateValue;
    [ObservableProperty] private bool _boolValue;

    public bool IsDate => DataType == "Date";
    public bool IsBoolean => DataType == "Boolean";
    // Either the field says it is a list, or its type already means it (MultiSelect, grandfathered) — #703.
    public bool IsMultiLine => IsList || DataType == "MultiSelect";
    public bool IsSingleLine => !IsDate && !IsBoolean && !IsMultiLine;
    public string Label => IsRequired ? $"{Name} *" : Name;

    public static MaskFieldEditViewModel Create(MasksClient.MaskFieldInfo definition, IReadOnlyList<string> values)
    {
        var field = new MaskFieldEditViewModel
        {
            FieldDefinitionId = definition.Id,
            Name = definition.Name,
            DataType = definition.DataType,
            IsRequired = definition.IsRequired,
            IsList = definition.IsList,
        };

        // Multiplicity is decided BEFORE the type: a list of dates is a list first, so it gets the list editor
        // rather than a date picker that could only ever hold one of them.
        if (field.IsMultiLine)
        {
            field.TextValue = string.Join("\n", values);
            return field;
        }

        switch (definition.DataType)
        {
            case "Date":
                field.DateValue = values.Count > 0 && System.DateTimeOffset.TryParse(values[0], out var d)
                    ? new System.DateTimeOffset(d.Date, System.TimeSpan.Zero) : null;
                break;
            case "Boolean":
                field.BoolValue = values.Count > 0 && values[0].Equals("true", System.StringComparison.OrdinalIgnoreCase);
                break;
            default:
                field.TextValue = values.Count > 0 ? values[0] : "";
                break;
        }

        return field;
    }

    public IReadOnlyList<string> ToValues() => IsMultiLine
        ? TextValue.Split('\n').Select(v => v.Trim()).Where(v => v.Length > 0).ToList()
        : DataType switch
        {
            "Date" => DateValue is { } d ? [d.ToString("yyyy-MM-dd")] : [],
            "Boolean" => [BoolValue ? "true" : "false"],
            _ => string.IsNullOrWhiteSpace(TextValue) ? [] : [TextValue.Trim()],
        };
}

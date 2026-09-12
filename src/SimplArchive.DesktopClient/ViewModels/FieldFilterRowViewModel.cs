using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SimplArchive.DesktopClient.ViewModels;

// An operator choice for a field filter — Value is the API operator (eq/gt/…), Label is the display text.
public sealed record OperatorOption(string Value, string Label);

// One repeatable index-field filter row in the desktop search-refinement panel (ADR "Search-refinement UI"):
// a field, a type-appropriate operator, and a value. The operators and which value input shows adapt to the
// selected field's DataType (Text=0, Number=1, Date=2, DateTime=3, Boolean=4, SingleSelect=5,
// MultiSelect=6) — pinned to the enum by FieldDataTypeWireValuesTests after these drifted (#1128).
public partial class FieldFilterRowViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<string, int> _typesByField;

    public FieldFilterRowViewModel(IReadOnlyList<string> fieldNames, IReadOnlyDictionary<string, int> typesByField)
    {
        FieldNames = fieldNames;
        _typesByField = typesByField;
        _fieldName = fieldNames.Count > 0 ? fieldNames[0] : "";
        UpdateForField();
    }

    public IReadOnlyList<string> FieldNames { get; }

    public ObservableCollection<OperatorOption> Operators { get; } = [];

    [ObservableProperty] private string _fieldName;
    [ObservableProperty] private OperatorOption? _selectedOperator;
    [ObservableProperty] private string _value = string.Empty;
    [ObservableProperty] private DateTime? _dateValue;
    [ObservableProperty] private bool _booleanValue;
    [ObservableProperty] private bool _isDate;
    [ObservableProperty] private bool _isBoolean;
    [ObservableProperty] private bool _isTextLike = true;

    public int DataType { get; private set; }

    // The value to send (empty ⇒ skip this row).
    public string WireValue => IsDate
        ? DateValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
        : IsBoolean ? (BooleanValue ? "true" : "false") : Value.Trim();

    partial void OnFieldNameChanged(string value) => UpdateForField();

    private void UpdateForField()
    {
        DataType = _typesByField.TryGetValue(FieldName, out var type) ? type : 0;

        Operators.Clear();
        foreach (var op in OperatorsFor(DataType))
        {
            Operators.Add(op);
        }

        SelectedOperator = Operators.Count > 0 ? Operators[0] : null;
        IsDate = DataType is 2 or 3;
        IsBoolean = DataType == 3;
        IsTextLike = !IsDate && !IsBoolean;
        Value = string.Empty;
        DateValue = null;
        BooleanValue = false;
    }

    public static IReadOnlyList<OperatorOption> OperatorsFor(int dataType) => dataType switch
    {
        1 => [new("eq", "="), new("gt", ">"), new("gte", "≥"), new("lt", "<"), new("lte", "≤")],
        // A DateTime (3) is a MOMENT, filtered by day like a Date — see the web twin for why these numbers
        // moved (#1128): DateTime was inserted at 3 and shifted Boolean, SingleSelect and MultiSelect up one.
        2 or 3 => [new("eq", "on"), new("gte", "on/after"), new("lte", "on/before"), new("gt", "after"), new("lt", "before")],
        4 => [new("eq", "is")],
        5 or 6 => [new("eq", "is"), new("in", "is any of")],
        _ => [new("contains", "contains"), new("eq", "equals")],
    };
}

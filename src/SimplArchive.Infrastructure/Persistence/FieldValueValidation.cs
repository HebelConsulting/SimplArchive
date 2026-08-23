using System.Globalization;
using SimplArchive.Domain.Masks;

namespace SimplArchive.Infrastructure.Persistence;

/// <summary>
/// Whether one index-field value satisfies its field definition's per-type constraints (ADR 0162).
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <see cref="SimplArchiveDbContext"/>, which calls it from <c>SaveChanges</c> — still the
/// sole enforcement point, unchanged. What moved is a pure function over two entities that never touched the
/// context's state, and it moved because the DbContext is on the 1000-line standing-debt list: adding the
/// e-mail arm to it there would have grown a file that may only shrink (#703, #466).
/// </para>
/// <para>
/// It throws a bare <see cref="InvalidOperationException"/> deliberately, as it always has — the Api boundary
/// catches it and translates it into <c>FIELD_VALUE_INVALID</c>, which is the DbContext-invariant convention
/// CLAUDE.md records as this codebase's one exception to the specific-exception rule.
/// </para>
/// </remarks>
internal static class FieldValueValidation
{
    public static void EnsureValid(FieldValue fieldValue, FieldDefinition fieldDefinition)
    {
        switch (fieldDefinition.DataType)
        {
            case FieldDataType.Text:
                if (fieldDefinition.MaxTextLength is { } maxLength && fieldValue.Value.Length > maxLength)
                {
                    throw new InvalidOperationException(
                        $"Field value for '{fieldDefinition.Name}' exceeds the maximum length of {maxLength}.");
                }

                if (fieldDefinition.FormatPattern is { } pattern && !System.Text.RegularExpressions.Regex.IsMatch(fieldValue.Value, pattern))
                {
                    throw new InvalidOperationException(
                        $"Field value '{fieldValue.Value}' for '{fieldDefinition.Name}' does not match the required format.");
                }

                break;

            case FieldDataType.Number:
                var numberValue = decimal.Parse(fieldValue.Value, CultureInfo.InvariantCulture);

                if (fieldDefinition.MinValue is { } minNumberText
                    && numberValue < decimal.Parse(minNumberText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {numberValue} for '{fieldDefinition.Name}' is below the minimum of {minNumberText}.");
                }

                if (fieldDefinition.MaxValue is { } maxNumberText
                    && numberValue > decimal.Parse(maxNumberText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {numberValue} for '{fieldDefinition.Name}' is above the maximum of {maxNumberText}.");
                }

                break;

            // DateTime rides with Date: both parse as a DateTimeOffset, and the min/max comparison below is
            // the same question asked of a point in time rather than of a day (#660).
            case FieldDataType.Date:
            case FieldDataType.DateTime:
                var dateValue = DateTimeOffset.Parse(fieldValue.Value, CultureInfo.InvariantCulture);

                if (fieldDefinition.MinValue is { } minDateText
                    && dateValue < DateTimeOffset.Parse(minDateText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {dateValue:O} for '{fieldDefinition.Name}' is before the minimum of {minDateText}.");
                }

                if (fieldDefinition.MaxValue is { } maxDateText
                    && dateValue > DateTimeOffset.Parse(maxDateText, CultureInfo.InvariantCulture))
                {
                    throw new InvalidOperationException(
                        $"Field value {dateValue:O} for '{fieldDefinition.Name}' is after the maximum of {maxDateText}.");
                }

                break;

            // An address is validated for SHAPE, per value (#703). This is the seam a list rides for free:
            // it already runs once per FieldValue row, and a list is n rows, so every element is checked
            // with no multiplicity logic here at all.
            case FieldDataType.EmailAddress:
                if (!EmailAddressValue.IsWellFormed(fieldValue.Value))
                {
                    throw new InvalidOperationException(
                        $"Field value '{fieldValue.Value}' for '{fieldDefinition.Name}' is not a valid e-mail address.");
                }

                break;

            case FieldDataType.Boolean:
            case FieldDataType.SingleSelect:
            case FieldDataType.MultiSelect:
                // No Format/Range constraints apply to these data types (ADR "Metadata field validation
                // rules" only defines Format for Text and Range for Number/Date).
                break;
        }
    }
}

using System.Globalization;
using System.Reflection;
using SimplArchive.Domain.Masks;
using SimplArchive.Localization;

namespace SimplArchive.UnitTests;

// The manuals masks' names are stored in English and translated where they are read (ADR 0891). Every field the
// Manual mask declares must be one the reader sees in their language — a field added to ManualFields without an entry
// in WellKnownLabels would silently read English to everyone, which is what this pins.
public class WellKnownLabelsTests
{
    private static IEnumerable<string> ManualFieldNames() =>
        typeof(WellKnownMaskIds.ManualFields).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void Every_manual_field_and_both_mask_names_read_in_german()
    {
        var names = ManualFieldNames().Concat(["Manual", "SimplArchive Documentation"]).ToList();
        Assert.Equal(10, names.Count); // eight fields, two masks — the scan found them all

        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de");
            foreach (var name in names)
            {
                Assert.NotEqual(name, WellKnownLabels.For(name));
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Any_other_name_is_shown_exactly_as_stored()
    {
        Assert.Equal("Invoice number", WellKnownLabels.For("Invoice number"));
        Assert.Equal(string.Empty, WellKnownLabels.For(null));
    }
}

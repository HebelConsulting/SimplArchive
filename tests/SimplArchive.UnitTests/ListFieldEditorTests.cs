using SimplArchive.Client.Models;

namespace SimplArchive.UnitTests;

// How a LIST field is edited in the web client's index-data form (#703). The desktop's MaskFieldEditViewModel
// is the same shape and is asserted separately — one surface, two renderings (ADR 0511), so a divergence has
// to break a test on one side rather than show up as two clients that disagree about a field.
public class ListFieldEditorTests
{
    private static MaskFieldInfo Definition(string dataType, bool isList) =>
        new() { Id = Guid.NewGuid(), Name = "eMail Addresses", DataType = dataType, IsList = isList };

    [Fact]
    public void A_list_field_round_trips_through_the_multi_line_editor()
    {
        var field = EditField.Create(Definition("EmailAddress", isList: true), ["a@x.dev", "b@x.dev"]);

        Assert.True(field.IsMultiLine);
        Assert.False(field.IsSingleLine);
        Assert.Equal("a@x.dev\nb@x.dev", field.TextValue);
        Assert.Equal(["a@x.dev", "b@x.dev"], field.ToValues());
    }

    [Fact]
    public void A_blank_line_and_stray_spacing_do_not_become_values()
    {
        var field = EditField.Create(Definition("EmailAddress", isList: true), []);
        field.TextValue = "  a@x.dev  \n\n\n  b@x.dev\n";

        // Not cosmetic: an empty element would be stored as an empty FieldValue row, and a padded one would
        // fail the address shape — both from a user simply pressing Enter twice.
        Assert.Equal(["a@x.dev", "b@x.dev"], field.ToValues());
    }

    [Fact]
    public void The_same_type_without_the_flag_stays_a_single_value()
    {
        var field = EditField.Create(Definition("EmailAddress", isList: false), ["a@x.dev"]);

        // The point of the pair: multiplicity comes from the FLAG, not from the type. EmailAddress is not
        // inherently a list, so an EmailAddress field that never asked for one must not get the list editor.
        Assert.False(field.IsMultiLine);
        Assert.True(field.IsSingleLine);
        Assert.Equal(["a@x.dev"], field.ToValues());
    }

    [Fact]
    public void MultiSelect_is_still_a_list_without_the_flag()
    {
        // Grandfathered: it is a list by virtue of its type, so no tenant has to set IsList on the fields it
        // already has. Dropping this arm while introducing the flag would quietly break every existing one.
        var field = EditField.Create(Definition("MultiSelect", isList: false), ["finance", "quarterly"]);

        Assert.True(field.IsMultiLine);
        Assert.Equal(["finance", "quarterly"], field.ToValues());
    }

    [Fact]
    public void A_list_of_dates_is_a_list_first()
    {
        // Multiplicity is decided BEFORE the type. A date picker can hold exactly one date, so a Date field
        // marked as a list must not get one — it would silently discard every value but the first.
        var field = EditField.Create(Definition("Date", isList: true), ["2026-08-01", "2026-09-01"]);

        Assert.True(field.IsDate);        // it is still a Date field...
        Assert.True(field.IsMultiLine);   // ...but the list editor wins,
        Assert.Equal(["2026-08-01", "2026-09-01"], field.ToValues()); // ...so both values survive.
    }

    [Fact]
    public void A_single_valued_field_of_each_kind_is_untouched()
    {
        Assert.Equal(["2026-08-01"], EditField.Create(Definition("Date", isList: false), ["2026-08-01"]).ToValues());
        Assert.Equal(["true"], EditField.Create(Definition("Boolean", isList: false), ["true"]).ToValues());
        Assert.Equal(["hello"], EditField.Create(Definition("Text", isList: false), ["hello"]).ToValues());
        Assert.Empty(EditField.Create(Definition("Text", isList: false), []).ToValues());
    }
}

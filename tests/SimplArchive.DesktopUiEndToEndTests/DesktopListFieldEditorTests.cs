using SimplArchive.DesktopClient.Services;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The desktop half of #703's list editor. The web half is asserted by ListFieldEditorTests over the same
// cases, deliberately: the two clients render one surface (ADR 0511), and the way they came to disagree
// before was each deriving a display rule for itself (#671). Here the rule is IsMultiLine, and it has to
// answer identically on both sides — so the cases are the same cases, not merely similar ones.
public class DesktopListFieldEditorTests
{
    private static MasksClient.MaskFieldInfo Definition(string dataType, bool isList) =>
        new(Guid.NewGuid(), "eMail Addresses", dataType, IsRequired: false, IsList: isList);

    [Fact]
    public void A_list_field_round_trips_through_the_multi_line_editor()
    {
        var field = MaskFieldEditViewModel.Create(Definition("EmailAddress", isList: true), ["a@x.dev", "b@x.dev"]);

        Assert.True(field.IsMultiLine);
        Assert.False(field.IsSingleLine);
        Assert.Equal("a@x.dev\nb@x.dev", field.TextValue);
        Assert.Equal(["a@x.dev", "b@x.dev"], field.ToValues());
    }

    [Fact]
    public void A_blank_line_and_stray_spacing_do_not_become_values()
    {
        var field = MaskFieldEditViewModel.Create(Definition("EmailAddress", isList: true), []);
        field.TextValue = "  a@x.dev  \n\n\n  b@x.dev\n";

        Assert.Equal(["a@x.dev", "b@x.dev"], field.ToValues());
    }

    [Fact]
    public void The_same_type_without_the_flag_stays_a_single_value()
    {
        var field = MaskFieldEditViewModel.Create(Definition("EmailAddress", isList: false), ["a@x.dev"]);

        Assert.False(field.IsMultiLine);
        Assert.True(field.IsSingleLine);
        Assert.Equal(["a@x.dev"], field.ToValues());
    }

    [Fact]
    public void MultiSelect_is_still_a_list_without_the_flag()
    {
        var field = MaskFieldEditViewModel.Create(Definition("MultiSelect", isList: false), ["finance", "quarterly"]);

        Assert.True(field.IsMultiLine);
        Assert.Equal(["finance", "quarterly"], field.ToValues());
    }

    [Fact]
    public void A_list_of_dates_is_a_list_first()
    {
        // A DatePicker holds exactly one date, so a Date field marked as a list must not get one — it would
        // silently discard every value but the first.
        var field = MaskFieldEditViewModel.Create(Definition("Date", isList: true), ["2026-08-01", "2026-09-01"]);

        Assert.True(field.IsDate);
        Assert.True(field.IsMultiLine);
        Assert.Equal(["2026-08-01", "2026-09-01"], field.ToValues());
    }

    [Fact]
    public void A_single_valued_field_of_each_kind_is_untouched()
    {
        Assert.Equal(["2026-08-01"], MaskFieldEditViewModel.Create(Definition("Date", isList: false), ["2026-08-01"]).ToValues());
        Assert.Equal(["true"], MaskFieldEditViewModel.Create(Definition("Boolean", isList: false), ["true"]).ToValues());
        Assert.Equal(["hello"], MaskFieldEditViewModel.Create(Definition("Text", isList: false), ["hello"]).ToValues());
        Assert.Empty(MaskFieldEditViewModel.Create(Definition("Text", isList: false), []).ToValues());
    }
}

using SimplArchive.Domain.Masks;

namespace SimplArchive.UnitTests;

// FieldDataType's NUMBERS are a wire contract, and both clients branch on them (#1128).
//
// GET /api/search/fields sends the type as an integer, and the search-refinement UI in each client maps that
// integer to the operators and the input control to offer. So the enum's numeric values are not an internal
// detail: inserting a member in the middle renumbers everything after it, on the wire, silently.
//
// That happened. DateTime went in at 3 (#660) and pushed Boolean, SingleSelect and MultiSelect up one, while
// both clients kept the old mapping — so a DateTime field offered Boolean's single "is" and no date input, a
// Boolean offered "is any of", and a MultiSelect fell through to free-text operators. Nothing threw, no test
// noticed, and the filters were simply wrong for three of seven types until somebody read the enum.
//
// This pins the numbers. WHEN IT FAILS: a member was added in the middle. Add it at the END instead, or
// renumber deliberately AND update every site listed below in the same commit:
//
//   src/SimplArchive.Client/Components/Tabs/SearchTab.razor        OperatorsFor + the input branches
//   src/SimplArchive.Client/Services/SearchState.cs                the documented mapping
//   src/SimplArchive.DesktopClient/ViewModels/FieldFilterRowViewModel.cs  OperatorsFor + IsDate
//   src/SimplArchive.Api/Controllers/SearchController.cs           the documented mapping
public class FieldDataTypeWireValuesTests
{
    [Theory]
    [InlineData(FieldDataType.Text, 0)]
    [InlineData(FieldDataType.Number, 1)]
    [InlineData(FieldDataType.Date, 2)]
    [InlineData(FieldDataType.DateTime, 3)]
    [InlineData(FieldDataType.Boolean, 4)]
    [InlineData(FieldDataType.SingleSelect, 5)]
    [InlineData(FieldDataType.MultiSelect, 6)]
    [InlineData(FieldDataType.EmailAddress, 7)]
    [InlineData(FieldDataType.Url, 8)]
    [InlineData(FieldDataType.DocumentReference, 9)]
    public void The_wire_value_is_what_the_clients_branch_on(FieldDataType type, int wireValue) =>
        Assert.Equal(wireValue, (int)type);

    // ...and the count, so a member APPENDED at the end is noticed too. Appending is safe for the mapping
    // above, but a client that has never heard of the new type falls to its default branch — which is the
    // right failure only if somebody decided it was.
    [Fact]
    public void A_new_type_has_to_be_taught_to_the_clients() =>
        Assert.Equal(10, Enum.GetValues<FieldDataType>().Length);
}

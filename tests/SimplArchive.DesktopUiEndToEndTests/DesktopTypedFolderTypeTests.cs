using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.UiEndToEndTests;

// The Type column tells a typed folder's mask name, not the generic word (#824). The server has always sent
// the mask name in DocumentType; the clients flattened every folder to "Folder" — a rule written before typed
// folders existed. Only the genuine plain Folder (or a maskless one) keeps the localised generic word.
public class DesktopTypedFolderTypeTests
{
    private static NodeViewModel Folder(string documentType) => new()
    {
        Id = Guid.NewGuid(),
        Name = "x",
        HasChildren = false,
        HasVersions = false,
        DocumentType = documentType,
    };

    [Theory]
    [InlineData("Addressbook", "Addressbook")]
    [InlineData("Calendar", "Calendar")]
    [InlineData("Notebook", "Notebook")]
    [InlineData("Section", "Section")]
    [InlineData("Folder", "Folder")]   // the plain case keeps the (localised) generic word
    [InlineData("", "Folder")]         // …and so does a maskless folder from a pre-mask deployment
    public void A_typed_folder_names_its_mask(string documentType, string expected)
        => Assert.Equal(expected, Folder(documentType).TypeText);

    [Fact]
    public void A_document_still_names_its_type()
    {
        var doc = new NodeViewModel
        {
            Id = Guid.NewGuid(),
            Name = "x",
            HasChildren = false,
            HasVersions = true,
            DocumentType = "eMail",
        };
        Assert.Equal("eMail", doc.TypeText);
    }
}

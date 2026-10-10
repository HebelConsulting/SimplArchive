using System.Text.Json;
using SimplArchive.ApiClient;

namespace SimplArchive.UnitTests.ApiClient;

// A row is a folder by what the SERVER says from its mask (#1708), and only by "has no file" when an older server
// sends no flag. A file-less record (a push key) is a document even though it has no version.
public sealed class RowFolderFlagTests
{
    private static JsonElement Row(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_servers_flag_decides_and_its_absence_keeps_the_old_reading()
    {
        Assert.False(new Node(Guid.NewGuid(), "Push key", false, HasVersions: false, false, false, Folder: RowFlags.IsFolderOf(Row("""{"isFolder":false}"""))).IsFolder);
        Assert.True(new Node(Guid.NewGuid(), "Folder", false, HasVersions: false, false, false, Folder: RowFlags.IsFolderOf(Row("""{"isFolder":true}"""))).IsFolder);
        Assert.True(new Node(Guid.NewGuid(), "Old server", false, HasVersions: false, false, false, Folder: RowFlags.IsFolderOf(Row("{}"))).IsFolder);
        Assert.False(new Node(Guid.NewGuid(), "Old server doc", false, HasVersions: true, false, false, Folder: RowFlags.IsFolderOf(Row("{}"))).IsFolder);
    }
}

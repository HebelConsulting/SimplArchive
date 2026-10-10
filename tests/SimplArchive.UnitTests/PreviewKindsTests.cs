using SimplArchive.Presentation;

namespace SimplArchive.UnitTests;

// How a preview renders, as the desktop and the phone both decide it: the bytes first, the served type second.
public sealed class PreviewKindsTests
{
    [Theory]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D }, "application/octet-stream", PreviewKind.Pdf)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "application/octet-stream", PreviewKind.Image)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "application/octet-stream", PreviewKind.Image)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38 }, "application/octet-stream", PreviewKind.Image)]
    public void Magic_bytes_win_over_a_storage_served_octet_stream(byte[] bytes, string contentType, PreviewKind kind) =>
        Assert.Equal(kind, PreviewKinds.Of(bytes, contentType));

    [Theory]
    [InlineData("image/tiff", PreviewKind.Image)]
    [InlineData("application/pdf", PreviewKind.Pdf)]
    [InlineData("text/plain", PreviewKind.Text)]
    [InlineData("application/json", PreviewKind.Text)]
    [InlineData("application/xml", PreviewKind.Text)]
    [InlineData("application/octet-stream", PreviewKind.Unsupported)]
    public void Without_a_signature_the_served_type_decides(string contentType, PreviewKind kind) =>
        Assert.Equal(kind, PreviewKinds.Of("abcd"u8.ToArray(), contentType));
}

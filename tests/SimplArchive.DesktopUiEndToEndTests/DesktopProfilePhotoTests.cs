using System.Buffers.Binary;
using SimplArchive.DesktopClient;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.UiEndToEndTests;

// The profile-photo endpoints (ADR "User profile photo") driven through the real DesktopClient api client
// against the running API: set / get (round-trips the bytes) / delete on a throwaway user (admin path), the
// server-side PNG validation (non-PNG + oversized-dimension rejected), and the self path (me/photo →
// whoami.HasPhoto). Uses crafted PNG-header bytes — the server validates the header and stores bytes
// verbatim, so they need not be a renderable image.
[Collection(UiCollection.Name)]
public class DesktopProfilePhotoTests
{
    private readonly SelfHostedAppFixture _app;

    public DesktopProfilePhotoTests(SelfHostedAppFixture app) => _app = app;

    [Fact]
    public async Task Set_get_delete_and_validate_profile_photos()
    {
        DesktopClientOptions.ApiBaseUrl = _app.BaseUrl;
        var client = new SimplArchiveApiClient(await Ui.GetUserTokenAsync(_app.BaseUrl));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        // A throwaway user (admin path), so this doesn't touch the shared demo admin.
        var userId = await client.CreateUserAsync($"photo-{suffix}@example.test", "Photo User " + suffix);
        Assert.Null(await client.GetUserPhotoAsync(userId));

        // Set → GET round-trips the exact bytes.
        var png = Png(256, 256);
        await client.SetUserPhotoAsync(userId, png);
        Assert.Equal(png, await client.GetUserPhotoAsync(userId));

        // Server-side validation: a non-PNG and an over-large dimension are both rejected.
        await Assert.ThrowsAsync<ApiActionException>(() => client.SetUserPhotoAsync(userId, [1, 2, 3, 4]));
        await Assert.ThrowsAsync<ApiActionException>(() => client.SetUserPhotoAsync(userId, Png(2049, 2049)));

        // Delete → gone.
        await client.DeleteUserPhotoAsync(userId);
        Assert.Null(await client.GetUserPhotoAsync(userId));

        // Self path: me/photo sets the caller's own photo → whoami reports it; clean up afterwards.
        var me = (await client.GetWhoAmIAsync()).UserId!.Value;
        await client.SetMyPhotoAsync(Png(256, 256));
        Assert.True((await client.GetWhoAmIAsync()).HasPhoto);
        await client.DeleteUserPhotoAsync(me);
        Assert.False((await client.GetWhoAmIAsync()).HasPhoto);
    }

    // A minimal PNG header: 8-byte signature + an IHDR chunk carrying width/height — all the validator reads.
    private static byte[] Png(uint width, uint height)
    {
        var b = new byte[24];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0); // signature
        b[11] = 0x0D;                                                                 // IHDR length = 13
        new byte[] { 0x49, 0x48, 0x44, 0x52 }.CopyTo(b, 12);                          // "IHDR"
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20, 4), height);
        return b;
    }
}

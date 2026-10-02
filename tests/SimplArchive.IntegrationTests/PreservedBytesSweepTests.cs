using Microsoft.Extensions.Logging.Abstractions;
using SimplArchive.Application.Abstractions;
using SimplArchive.Infrastructure.Intray;

namespace SimplArchive.IntegrationTests;

// Expiring the bytes an intray or check-out overwrite set aside (#799).
//
// The preservation net has written into inbox-previous/ and stash-previous/ since #794 and nothing has ever
// read or removed them, so they accumulated in every tenant's bucket for as long as the installation ran.
public class PreservedBytesSweepTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid User = Guid.NewGuid();

    private static string Inbox(string name) => ObjectKeyPrefixes.UserInboxPrevious(Tenant, User) + name;

    private static string Stash(string name) => ObjectKeyPrefixes.UserStashPrevious(Tenant, User) + name;

    private static async Task<InMemoryObjectStorage> WithAsync(params (string Key, DateTimeOffset When)[] objects)
    {
        var storage = new InMemoryObjectStorage();
        foreach (var (key, when) in objects)
        {
            using var content = new MemoryStream("bytes"u8.ToArray());
            await storage.PutObjectAsync(key, content, "application/octet-stream");
            storage.Modified[key] = when;
        }

        return storage;
    }

    [Fact]
    public async Task A_copy_past_its_lifetime_is_removed()
    {
        var old = DateTimeOffset.UtcNow - PreservedBytesSweepWorker.Lifetime - TimeSpan.FromDays(1);
        var storage = await WithAsync((Inbox("scan.tif"), old));

        await PreservedBytesSweep.RunAsync(storage, [(Tenant, User)], DateTimeOffset.UtcNow, NullLogger.Instance);

        Assert.DoesNotContain(Inbox("scan.tif"), storage.Objects.Keys);
    }

    [Fact]
    public async Task A_copy_inside_its_lifetime_is_kept()
    {
        // The window is the whole value of the net: expiring early would throw away the copy somebody is about
        // to ask for, and they only ask once they have noticed the loss.
        var recent = DateTimeOffset.UtcNow - TimeSpan.FromDays(29);
        var storage = await WithAsync((Inbox("scan.tif"), recent));

        await PreservedBytesSweep.RunAsync(storage, [(Tenant, User)], DateTimeOffset.UtcNow, NullLogger.Instance);

        Assert.Contains(Inbox("scan.tif"), storage.Objects.Keys);
    }

    [Fact]
    public async Task The_check_out_stash_is_swept_too()
    {
        // Two names for one net, added by the same change (#794). Sweeping only the intray is how half a policy
        // ships and nobody notices the half that did not — the stash area is the less visible of the two.
        var old = DateTimeOffset.UtcNow - PreservedBytesSweepWorker.Lifetime - TimeSpan.FromDays(1);
        var storage = await WithAsync((Inbox("a.tif"), old), (Stash("b.docx"), old));

        await PreservedBytesSweep.RunAsync(storage, [(Tenant, User)], DateTimeOffset.UtcNow, NullLogger.Instance);

        Assert.DoesNotContain(storage.Objects.Keys, k => k.Contains("-previous/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_outside_the_preserved_areas_is_touched()
    {
        // The sweep deletes, so the blast radius is the assertion that matters: a prefix typo here would
        // quietly remove live intray items, which have no versions and no soft-delete to fall back on.
        var old = DateTimeOffset.UtcNow - PreservedBytesSweepWorker.Lifetime - TimeSpan.FromDays(1);
        var live = ObjectKeyPrefixes.UserInbox(Tenant, User) + "live.tif";
        var storage = await WithAsync((Inbox("gone.tif"), old), (live, old));

        await PreservedBytesSweep.RunAsync(storage, [(Tenant, User)], DateTimeOffset.UtcNow, NullLogger.Instance);

        Assert.Contains(live, storage.Objects.Keys);
        Assert.DoesNotContain(Inbox("gone.tif"), storage.Objects.Keys);
    }
}

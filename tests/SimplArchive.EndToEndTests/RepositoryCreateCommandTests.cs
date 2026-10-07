using SimplArchive.Cli.Commands;
using SimplArchive.Cli.Infrastructure;

namespace SimplArchive.EndToEndTests;

/// <summary>
/// <c>saconsole repository create</c> is find-or-create, so a bootstrap that died half-way can simply be run again.
/// </summary>
[Collection(E2ECollection.Name)]
[Trait("Area", "e2e-1")]
public class RepositoryCreateCommandTests(E2EApiFactory factory)
{
    [Fact]
    public async Task Creating_the_same_name_twice_finds_the_first_rather_than_failing_or_duplicating()
    {
        var (clientId, secret, _) = await factory.SeedServiceAccountAsync(canManageRepositories: true);
        using var http = factory.CreateAuthedClient(await factory.GetTokenAsync(clientId, secret));
        var api = new SimplArchiveApi(http);
        var name = $"Customers {Guid.NewGuid():N}";

        var first = await RepositoryCreateCommand.FindOrCreateAsync(api, name, CancellationToken.None);
        var second = await RepositoryCreateCommand.FindOrCreateAsync(api, name, CancellationToken.None);

        Assert.True(first.Created);
        Assert.Equal((first.Id, false), second);

        // A near miss is a different repository: the match is exact, never a guess.
        var other = await RepositoryCreateCommand.FindOrCreateAsync(api, name.ToUpperInvariant(), CancellationToken.None);
        Assert.True(other.Created);
        Assert.NotEqual(first.Id, other.Id);
    }
}

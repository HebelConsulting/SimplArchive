using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SimplArchive.Api.Concurrency;
using SimplArchive.Api.Errors.Exceptions.Concurrency;
using SimplArchive.Domain.Documents;
using SimplArchive.Domain.Tenants;
using SimplArchive.Domain.Users;
using SimplArchive.Infrastructure.Persistence;

namespace SimplArchive.IntegrationTests;

// WHEN the verb contract states the caller's precondition, for the case where `apply` SAVES (#1171, #1175).
//
// These exist because the controller-level tests CANNOT see this. Every structured-item endpoint checks
// If-Match itself at the top of the request, before writing anything to object storage, and that pre-flight
// answers 412 for the ordinary stale-tag case on its own. So the whole ItemSourceTests class — including the
// test named "a stale token loses" — passes identically with the contract's gate applied BEFORE apply, AFTER
// apply, or (measured) with the ordering deliberately flipped to the broken one. A mechanism no test can
// distinguish from its own absence is one nobody can safely change later.
//
// The thing that actually differs is only visible when `apply` writes the entity: DocumentFinalizer saves
// seven times and several of those write the document row. That is reproduced here directly rather than
// through a controller, because the point is the ORDERING, not the endpoint.
public class VerbContractGateOrderingTests
{
    private static SimplArchiveDbContext CreateContext(SqliteConnection connection, Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<SimplArchiveDbContext>()
            .UseSqlite(connection)
            .Options;

        return new SimplArchiveDbContext(options, new CurrentTenantAccessor { TenantId = tenantId });
    }

    private static HttpRequest RequestWith(Guid? ifMatch)
    {
        var context = new DefaultHttpContext();
        if (ifMatch is { } token)
        {
            context.Request.Headers["If-Match"] = $"\"{token}\"";
        }

        return context.Request;
    }

    /// <summary>A tenant, a user and one document — returned with the document's token as first stored.</summary>
    private static async Task<(Guid TenantId, Guid DocumentId, Guid Token)> SeedAsync(SqliteConnection connection)
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        using (var setup = CreateContext(connection, tenantId))
        {
            await setup.Database.EnsureCreatedAsync();
        }

        using (var seed = CreateContext(connection, tenantId))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "T", CreatedAt = DateTimeOffset.UtcNow });
            seed.Users.Add(new User { Id = userId, TenantId = tenantId, Email = "a@example.com", DisplayName = "A", CreatedAt = DateTimeOffset.UtcNow });
            seed.Documents.Add(new Document { Id = documentId, TenantId = tenantId, Name = "Doc", CreatedByUserId = userId, CreatedAt = DateTimeOffset.UtcNow });
            await seed.SaveChangesAsync();
        }

        using var read = CreateContext(connection, tenantId);
        var token = await read.Documents.Where(d => d.Id == documentId).Select(d => d.ConcurrencyToken).SingleAsync();
        return (tenantId, documentId, token);
    }

    // THE REGRESSION THIS FLAG EXISTS FOR.
    //
    // With the precondition stated AFTER apply, an apply that writes the entity has already moved the token by
    // the time we say which value the caller believed it was editing. The caller's tag — perfectly current when
    // the request arrived — is then compared against one the operation itself just minted, and the save is
    // refused. Every caller loses, including the only one that should have won.
    [Fact]
    public async Task Gating_after_an_apply_that_saves_refuses_a_caller_holding_the_CURRENT_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, documentId, token) = await SeedAsync(connection);

        using var dbContext = CreateContext(connection, tenantId);
        var verbs = new DocumentVerbs(dbContext);
        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId);

        await Assert.ThrowsAsync<EtagMismatchException>(() => verbs.MutateAsync(
            RequestWith(token), // the CURRENT token — this caller is not stale by any reading
            document,
            apply: async () =>
            {
                document.Name = "Renamed by the collaborator";
                await dbContext.SaveChangesAsync(); // the token moves HERE, before the gate is stated
            },
            gateBeforeApply: false));
    }

    // The same caller, the same saving apply, gated FIRST: the tag is handed to the collaborator's own save,
    // which is the first write of the whole mutation and therefore the honest place to judge it (ADR 0794 —
    // the tag must be the one the USER saw).
    [Fact]
    public async Task Gating_before_an_apply_that_saves_lets_the_current_token_through()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, documentId, token) = await SeedAsync(connection);

        using var dbContext = CreateContext(connection, tenantId);
        var verbs = new DocumentVerbs(dbContext);
        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId);

        await verbs.MutateAsync(
            RequestWith(token),
            document,
            apply: async () =>
            {
                document.Name = "Renamed by the collaborator";
                await dbContext.SaveChangesAsync();
            },
            gateBeforeApply: true);

        using var verify = CreateContext(connection, tenantId);
        var after = await verify.Documents.SingleAsync(d => d.Id == documentId);
        Assert.Equal("Renamed by the collaborator", after.Name);
        Assert.NotEqual(token, after.ConcurrencyToken); // the write happened, so the token moved
    }

    // And it still REFUSES a genuinely stale one. Without this the test above is satisfied by a gate that does
    // nothing at all, which is the failure mode the pre-flight check already hid once.
    [Fact]
    public async Task Gating_before_an_apply_that_saves_still_refuses_a_STALE_token()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, documentId, _) = await SeedAsync(connection);

        using var dbContext = CreateContext(connection, tenantId);
        var verbs = new DocumentVerbs(dbContext);
        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId);

        await Assert.ThrowsAsync<EtagMismatchException>(() => verbs.MutateAsync(
            RequestWith(Guid.NewGuid()), // a token nobody ever issued
            document,
            apply: async () =>
            {
                document.Name = "Renamed by the collaborator";
                await dbContext.SaveChangesAsync();
            },
            gateBeforeApply: true));
    }

    // The default ordering is not merely tolerated, it is CORRECT for the ordinary apply — the one that only
    // mutates the change tracker. Pinned so a future tidy-up cannot make gate-first unconditional and quietly
    // change when 37 other call sites state their precondition.
    [Fact]
    public async Task The_default_ordering_still_refuses_a_stale_token_on_an_apply_that_does_not_save()
    {
        using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var (tenantId, documentId, token) = await SeedAsync(connection);

        using var dbContext = CreateContext(connection, tenantId);
        var verbs = new DocumentVerbs(dbContext);
        var document = await dbContext.Documents.SingleAsync(d => d.Id == documentId);

        await Assert.ThrowsAsync<EtagMismatchException>(() => verbs.MutateAsync(
            RequestWith(Guid.NewGuid()),
            document,
            apply: () =>
            {
                document.Name = "Renamed in the change tracker only";
                return Task.CompletedTask;
            }));

        // …and the current token goes through, on a fresh context because the refusal above left the failed
        // original value on the tracked entry.
        using var second = CreateContext(connection, tenantId);
        var reloaded = await second.Documents.SingleAsync(d => d.Id == documentId);
        await new DocumentVerbs(second).MutateAsync(
            RequestWith(token),
            reloaded,
            apply: () =>
            {
                reloaded.Name = "Renamed in the change tracker only";
                return Task.CompletedTask;
            });

        using var verify = CreateContext(connection, tenantId);
        Assert.Equal("Renamed in the change tracker only", (await verify.Documents.SingleAsync(d => d.Id == documentId)).Name);
    }
}

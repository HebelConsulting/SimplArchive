namespace SimplArchive.EndToEndTests;

// All end-to-end test classes share ONE E2EApiFactory (a single set of Postgres/MinIO containers + one API
// host). Necessary because the factory configures the app via process-global environment variables — two
// fixtures would clobber each other's connection string. A shared collection also serializes the classes,
// avoiding cross-container interference. Each test still isolates itself with its own seeded tenant.
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<E2EApiFactory>
{
    public const string Name = "e2e";
}

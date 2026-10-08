using Xunit;

// One collection at a time (#1628). The suite was a single collection until the module-action tests got their own
// app (ModuleAppFixture), and two collections run IN PARALLEL by default: two full app stacks and two browsers
// booting together closed the second browser under its first test (TargetClosedException). Sequential collections
// also keep only one app alive at a time, since a collection's fixture is disposed when it finishes.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

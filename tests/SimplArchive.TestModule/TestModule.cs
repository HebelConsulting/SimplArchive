using Microsoft.Extensions.DependencyInjection;
using SimplArchive.ModuleAbi;

namespace SimplArchive.TestModule;

/// <summary>
/// The loader/seam/engine fixture: the smallest complete module — two masks, one machine, one fact
/// provider, one service. Its machine is deliberately the epic's hardest small shape (the medical): an
/// expiry, a temporarily-void flag that overrides valid dates, and an aggregate fact.
/// </summary>
public sealed class TestModule : IIndustryModule
{
    public static readonly Guid DossierMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000000");
    public static readonly Guid CertificateMaskId = Guid.Parse("7E57AB1E-0000-0000-0000-000000000001");

    public string ModuleId => "test-module";

    public string DisplayName => "Test Module";

    public int AbiMajorVersion => ModuleAbiVersion.Major;

    public IReadOnlyList<ModuleMaskSeed> Masks { get; } =
    [
        new ModuleMaskSeed(DossierMaskId, "Test Dossier", IsFolderMask: true, IsBookable: false, []),
        new ModuleMaskSeed(CertificateMaskId, "Test Certificate", IsFolderMask: false, IsBookable: false,
        [
            new ModuleFieldSeed("Valid to", "Date", IsRequired: false),
            new ModuleFieldSeed("Temporarily void", "Boolean", IsRequired: false),
        ]),
    ];

    public void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<TestModuleMarker>();
        services.AddSingleton<IModuleFactProvider, TestFactProvider>();
    }

    public void DefineStateMachines(IStateMachineDefinitions machines) =>
        machines.Machine("test-pilot", DossierMaskId)
            .Status("MayAct",
                StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DateNotPast, null,
                    "test.certificate-expired", "The certificate expired {value}."),
                // The epic's temporarily-void shape: a certificate can be suspended while its dates still
                // read valid — NotEquals holds for an absent flag (a checkbox never ticked is not "true").
                StateCondition.ChildField(CertificateMaskId, "Temporarily void", ConditionTest.NotEquals, "true",
                    "test.certificate-void", "The certificate is temporarily void."),
                StateCondition.FactAtLeast("testLandings", 3,
                    "test.recency", "{value} recent landings; 3 required."))
            .Transition("log-entry",
                [
                    StateCondition.ChildField(CertificateMaskId, "Valid to", ConditionTest.DateNotPast, null,
                        "test.certificate-expired", "The certificate expired {value}."),
                ],
                async context => await context.Archive.CreateDocumentAsync(
                    context.SubjectDocumentId, CertificateMaskId, $"Entry {Guid.NewGuid():N}"));
}

/// <summary>Registered by <see cref="TestModule.ConfigureServices"/> so a test can see the call happened.</summary>
public sealed class TestModuleMarker;

/// <summary>The aggregate family's fixture: a settable fact, so tests steer the recency verdict.</summary>
public sealed class TestFactProvider : IModuleFactProvider
{
    /// <summary>Settable by tests — a real provider computes from read models; this one is the dial.</summary>
    public static int Landings { get; set; } = 5;

    public IReadOnlyList<string> FactNames { get; } = ["testLandings"];

    public Task<FactValue> GetAsync(string factName, Guid subjectDocumentId, DateTimeOffset asOf, CancellationToken cancellationToken = default) =>
        Task.FromResult(new FactValue(Landings.ToString(), $"{Landings} landings in the window"));
}

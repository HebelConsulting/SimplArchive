using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetArchTest.Rules;
using SimplArchive.Api.Errors;

namespace SimplArchive.UnitTests;

// Enforces the Clean Architecture layering (ADR 0002) + a few placement conventions as executable rules, so a
// stray `using` or a misplaced type can't quietly violate the design. See ADR "Architecture tests (NetArchTest)".
public class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(SimplArchive.Domain.Abstractions.ITenantScoped).Assembly;
    private static readonly Assembly Application = typeof(SimplArchive.Application.Abstractions.ICurrentTenantAccessor).Assembly;
    private static readonly Assembly Infrastructure = typeof(SimplArchive.Infrastructure.Persistence.SimplArchiveDbContext).Assembly;
    private static readonly Assembly Api = typeof(ApiException).Assembly;

    // ---- Layer-dependency rules (dependencies point inward only) ------------------------------------------

    [Fact]
    public void Domain_depends_on_no_other_layer_and_no_EF_Core()
    {
        var result = Types.InAssembly(Domain)
            .Should().NotHaveDependencyOnAny(
                "SimplArchive.Application",
                "SimplArchive.Infrastructure",
                "SimplArchive.Api",
                "SimplArchive.Auth",
                "SimplArchive.Client",
                "Microsoft.EntityFrameworkCore")
            .GetResult();

        AssertOk(result, "Domain must have no dependency on any other layer (persistence-ignorant, no EF Core)");
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        var result = Types.InAssembly(Application)
            .Should().NotHaveDependencyOnAny(
                "SimplArchive.Infrastructure",
                "SimplArchive.Api",
                "SimplArchive.Auth",
                "SimplArchive.Client")
            .GetResult();

        AssertOk(result, "Application may depend only on Domain (not Infrastructure/Api/Auth/Client)");
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api_or_Auth()
    {
        var result = Types.InAssembly(Infrastructure)
            .Should().NotHaveDependencyOnAny("SimplArchive.Api", "SimplArchive.Auth", "SimplArchive.Client")
            .GetResult();

        AssertOk(result, "Infrastructure implements Application's interfaces; it must not reference Api/Auth/Client");
    }

    // ---- Placement / naming conventions --------------------------------------------------------------------

    [Fact]
    public void Controllers_end_with_Controller()
    {
        // ABSTRACT bases are excluded: the rule exists so a routable controller is recognisable by name, and an
        // abstract type serves no request — the DAV controllers share `DavControllerBase`, which is shared
        // plumbing, not an endpoint. A concrete controller still has to end in Controller.
        var predicate = Types.InAssembly(Api).That().Inherit(typeof(ControllerBase)).And().AreNotAbstract();
        AssertMatchedSomething(predicate, "concrete ControllerBase subclasses");

        AssertOk(predicate.Should().HaveNameEndingWith("Controller").GetResult(),
            "Every MVC controller (a concrete ControllerBase subclass) must be named *Controller");
    }

    [Fact]
    public void Api_exceptions_derive_from_ApiException()
    {
        var predicate = Types.InAssembly(Api)
            .That().ResideInNamespaceStartingWith("SimplArchive.Api.Errors.Exceptions").And().AreClasses();
        AssertMatchedSomething(predicate, "types under Api/Errors/Exceptions");

        AssertOk(predicate.Should().Inherit(typeof(ApiException)).GetResult(),
            "Every type under Api/Errors/Exceptions must derive from ApiException");
    }

    [Fact]
    public void EF_configurations_implement_IEntityTypeConfiguration()
    {
        var predicate = Types.InAssembly(Infrastructure)
            .That().ResideInNamespaceStartingWith("SimplArchive.Infrastructure.Persistence.Configurations").And().AreClasses();
        AssertMatchedSomething(predicate, "types under Infrastructure/Persistence/Configurations");

        AssertOk(predicate.Should().ImplementInterface(typeof(IEntityTypeConfiguration<>)).GetResult(),
            "Every type under Infrastructure/Persistence/Configurations must implement IEntityTypeConfiguration<T>");
    }

    // Guards against a vacuous pass: if a namespace/base type moves and the predicate matches zero types, the
    // rule would trivially "succeed" — so require it to have selected at least one type.
    private static void AssertMatchedSomething(NetArchTest.Rules.PredicateList predicate, string what) =>
        Assert.True(predicate.GetTypes().Any(), $"Architecture test matched no {what} — has the namespace/type moved?");

    private static void AssertOk(TestResult result, string rule)
    {
        Assert.True(
            result.IsSuccessful,
            rule + ". Violating types:\n" + string.Join("\n", result.FailingTypeNames ?? []));
    }
}

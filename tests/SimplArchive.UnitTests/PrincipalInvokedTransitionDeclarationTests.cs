using SimplArchive.Infrastructure.Modules;
using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

// ABI 1.6 (ADR 0897): a principal-invoked transition is refused at LOAD when its declaration cannot name anybody:
// an empty field list, or a field its subject mask does not declare. Either would read as "this person may sign"
// and behave as "nobody but an administrator may", found only by the first person who cannot sign.
public class PrincipalInvokedTransitionDeclarationTests
{
    private static readonly Guid SubjectMask = Guid.NewGuid();

    private static readonly IReadOnlyList<ModuleMaskSeed> Masks =
    [
        new ModuleMaskSeed(SubjectMask, "Entry", IsFolderMask: false, IsBookable: false,
            [new ModuleFieldSeed("Instructor", "Text"), new ModuleFieldSeed("Pilot", "Text")]),
    ];

    private static StateMachineCatalog Declare(IReadOnlyList<string> fields)
    {
        var catalog = new StateMachineCatalog();
        catalog.ForModule("m").Machine("entry", SubjectMask)
            .Transition("sign", "Sign", [], _ => Task.CompletedTask, invokedByPrincipalFields: fields);
        return catalog;
    }

    [Fact]
    public void Declared_fields_are_carried_in_precedence_order()
    {
        var catalog = Declare(["Instructor", "Pilot"]);
        catalog.ValidatePrincipalFields("m", Masks);

        Assert.Equal(["Instructor", "Pilot"], catalog.Machines["entry"].Transitions["sign"].InvokedByPrincipalFields);
    }

    [Fact]
    public void A_field_the_subject_mask_lacks_refuses_the_module_naming_the_field()
    {
        var catalog = Declare(["Instructor", "Pilto"]);

        var refused = Assert.Throws<UnknownPrincipalFieldException>(() => catalog.ValidatePrincipalFields("m", Masks));
        Assert.Contains("'Pilto'", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_subject_mask_the_module_does_not_declare_refuses_it_too() =>
        Assert.Throws<UnknownPrincipalFieldException>(() => Declare(["Pilot"]).ValidatePrincipalFields("m", []));

    [Fact]
    public void A_declaration_naming_nobody_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Declare([]));
        Assert.Throws<ArgumentException>(() => Declare([" "]));
    }

    [Fact]
    public void An_ordinary_transition_carries_no_principal_fields()
    {
        var catalog = new StateMachineCatalog();
        catalog.ForModule("m").Machine("entry", SubjectMask).Transition("sign", "Sign", [], _ => Task.CompletedTask);

        Assert.Null(catalog.Machines["entry"].Transitions["sign"].InvokedByPrincipalFields);
    }
}

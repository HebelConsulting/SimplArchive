using SimplArchive.ModuleAbi;

namespace SimplArchive.UnitTests;

/// <summary>
/// Where an IMAP reader's certificates come from (ADR 0855), and the distinction that is easy to lose.
/// </summary>
/// <remarks>
/// Written because the first draft of that change lost it. <c>ModuleReaderCertificates.ForAsync</c> answers
/// null for "no module asks this question" and an EMPTY LIST for "a module answered, and this reader holds
/// none" — and where a module answers it is the ONLY source (ADR 0842). Collapse the two into "no
/// certificates" and the fetch consults the encryption service's registry next, so a certificate the module
/// REVOKED goes on opening mail. That is the union ADR 0842 forbids, reached by accident rather than by
/// decision, and no test would have noticed.
/// </remarks>
public class ReaderCertificateSourceTests
{
    private static SimplArchive.Infrastructure.Modules.ReaderCertificateAnswer Module(params string[] pems) =>
        SimplArchive.Infrastructure.Modules.ReaderCertificateAnswer.Answered(
            [.. pems.Select(pem => new ReaderCertificate(pem, "device", DateTimeOffset.MaxValue))]);

    private static readonly SimplArchive.Infrastructure.Modules.ReaderCertificateAnswer NoModule =
        SimplArchive.Infrastructure.Modules.ReaderCertificateAnswer.NoModule;

    [Fact]
    public void A_modules_answer_is_used_and_no_other_source_may_be_consulted()
    {
        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(
            Module("card-pem", "laptop-pem"), columnPem: "column-pem");

        // The column is IGNORED, which is the point: not a fallback chain and not a union.
        Assert.Equal(["card-pem", "laptop-pem"], source.Pems);
        Assert.True(source.AnsweredByModule);
        Assert.True(source.Envelopes);
        Assert.False(source.MayConsultRegistry);
    }

    [Fact] // THE case the first draft got wrong
    public void A_module_answering_NONE_still_closes_every_other_source()
    {
        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(Module(), columnPem: "column-pem");

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);              // nothing to envelope to...
        Assert.False(source.MayConsultRegistry);     // ...and the registry must NOT be asked anyway
    }

    [Fact]
    public void With_no_module_the_self_service_column_answers()
    {
        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(NoModule, columnPem: "column-pem");

        Assert.Equal(["column-pem"], source.Pems);
        Assert.False(source.AnsweredByModule);
        Assert.True(source.Envelopes);
        Assert.True(source.MayConsultRegistry);
    }

    [Fact]
    public void With_no_module_and_no_column_the_registry_is_still_open()
    {
        // The pre-existing behaviour of an installation with neither: the FETCH path asks the encryption
        // service's registry, and falls back to plaintext if that has nothing either.
        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(NoModule, columnPem: null);

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);
        Assert.True(source.MayConsultRegistry);
    }

    [Theory]
    [InlineData(SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome.LicenceLapsed)]
    [InlineData(SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome.AskFailed)]
    public void A_lapsed_licence_or_a_failed_ask_also_closes_every_other_source(
        SimplArchive.Infrastructure.Modules.ReaderCertificateOutcome outcome)
    {
        // ADR 0859's addition to the same rule: neither of these said "this reader has none" — one says the
        // tenant is not licensed and the other that nothing could be asked — so falling through to the
        // column or the registry on either is the union ADR 0842 forbids, arrived at from a new direction.
        var answer = new SimplArchive.Infrastructure.Modules.ReaderCertificateAnswer(outcome, []);

        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(answer, columnPem: "column-pem");

        Assert.Empty(source.Pems);
        Assert.False(source.Envelopes);
        Assert.False(source.MayConsultRegistry);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void An_empty_column_is_the_same_as_no_column(string? columnPem)
    {
        var source = SimplArchive.Infrastructure.Modules.ReaderCertificateSource.Resolve(NoModule, columnPem);

        Assert.Empty(source.Pems);
        Assert.True(source.MayConsultRegistry);
    }
}

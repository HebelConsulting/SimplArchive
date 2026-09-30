using Microsoft.AspNetCore.Http;
using SimplArchive.Api.Documents;
using SimplArchive.Api.Errors.Exceptions.Encryption;
using SimplArchive.Infrastructure.Modules;

namespace SimplArchive.UnitTests;

/// <summary>
/// Which refusal a reader gets when there is no certificate to address (#1411, ADR 0859).
/// </summary>
/// <remarks>
/// ADR 0842 requires four refusals to stay distinguishable *because they have different fixes*, and warns
/// that collapsing them leaves an administrator with the one they cannot act on. They were collapsed: every
/// empty set threw `CONTENT_CANNOT_BE_ENVELOPED`, which tells the reader to "register a current certificate
/// and try again" — advice that is wrong and unactionable when the module is broken or unlicensed, and which
/// sends the wrong person to the wrong screen.
/// </remarks>
public class ReaderCertificateRefusalTests
{
    [Fact]
    public void A_broken_module_is_not_the_readers_fault_and_may_recover()
    {
        var refusal = Assert.IsType<ReaderCertificatesUnavailableException>(
            StrictEnvelopeDelivery.RefusalFor(ReaderCertificateOutcome.AskFailed));

        // 503, not 409: a broken module may recover on its own, so a client that retries later is behaving
        // correctly — which is the thing the status code itself has to say.
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        Assert.Equal("READER_CERTIFICATES_UNAVAILABLE", refusal.ErrorCode);
        Assert.Contains("Nothing is wrong with your certificate", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unlicensed_module_names_the_licence_rather_than_the_certificate()
    {
        var refusal = Assert.IsType<EncryptionModuleNotLicensedException>(
            StrictEnvelopeDelivery.RefusalFor(ReaderCertificateOutcome.LicenceLapsed));

        // 409, not 503: unlike a module that threw, this does not recover on its own — someone files a
        // licence, which is a change of state, which is what 409 means.
        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Equal("ENCRYPTION_MODULE_NOT_LICENSED", refusal.ErrorCode);
        Assert.Contains("administrator", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReaderCertificateOutcome.Answered)]
    [InlineData(ReaderCertificateOutcome.NoModule)]
    public void A_reader_who_genuinely_has_none_is_told_what_THEY_can_do(ReaderCertificateOutcome outcome)
    {
        // The two outcomes that really do mean "you have no usable certificate here": a module answered and
        // named none, or there is no module and neither the column nor the registry had one. Both are the
        // reader's to fix, which is the one case the old collapsed message was right about.
        var refusal = Assert.IsType<ContentCannotBeEnvelopedException>(
            StrictEnvelopeDelivery.RefusalFor(outcome));

        Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
        Assert.Contains("Register a current certificate", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_outcome_is_mapped_rather_than_falling_into_a_default_nobody_chose()
    {
        // The table must be total. A new outcome inheriting the reader-blaming message by falling into `_`
        // is exactly how the four refusals collapsed in the first place, so this enumerates the enum rather
        // than trusting the switch — and the three distinct types below are what say the mapping still
        // discriminates at all.
        var byOutcome = Enum.GetValues<ReaderCertificateOutcome>()
            .ToDictionary(outcome => outcome, outcome => StrictEnvelopeDelivery.RefusalFor(outcome).GetType());

        Assert.Equal(Enum.GetValues<ReaderCertificateOutcome>().Length, byOutcome.Count);
        Assert.Equal(3, byOutcome.Values.Distinct().Count());
    }
}

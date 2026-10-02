using Microsoft.AspNetCore.Http;
using SimplArchive.Application.Security;

namespace SimplArchive.Api.Errors.Exceptions.Principals;

/// <summary>
/// A chosen password the policy will not accept (#849, ADR 0065's other half).
/// </summary>
/// <remarks>
/// <para>
/// <b>One error code per refusal, because the fixes differ</b> (ADR 0859): too short is "type more", too long
/// is "that is not a password", and already-breached is "pick a different word" — and only the last of those
/// is surprising enough that a single generic message would send somebody to re-type the same thing with a
/// digit on the end, which is exactly what the check refused.
/// </para>
/// <para>
/// <b>Naming the reason is not an oracle here.</b> Every caller is already authenticated — a user changing
/// their own password, an administrator creating an account — so the refusal reveals nothing about who exists.
/// The detail deliberately never echoes the password, nor says WHICH list entry it reduced to: that would
/// teach an attacker the normalisation.
/// </para>
/// </remarks>
public sealed class PasswordRefusedException : PrincipalException
{
    public PasswordRefusedException(PasswordRefusal refusal)
        : base(CodeFor(refusal), StatusCodes.Status400BadRequest, DetailFor(refusal))
    {
    }

    private static string CodeFor(PasswordRefusal refusal) => refusal switch
    {
        PasswordRefusal.TooShort => "PASSWORD_TOO_SHORT",
        PasswordRefusal.TooLong => "PASSWORD_TOO_LONG",
        _ => "PASSWORD_TOO_COMMON",
    };

    private static string DetailFor(PasswordRefusal refusal) => refusal switch
    {
        PasswordRefusal.TooShort =>
            $"A password must be at least {PasswordPolicy.MinimumLength} characters.",
        PasswordRefusal.TooLong =>
            $"A password may be at most {PasswordPolicy.MaximumLength} characters.",

        // Says what to do, and says why the obvious fix is not one: adding a digit or a punctuation mark is
        // precisely the decoration the check sees through, so a message that did not say so would send people
        // round the same loop.
        _ => "That password appears on a list of commonly used passwords, so it is among the first an attacker "
            + "tries. Adding a digit or a symbol to it will not help — choose a different set of words.",
    };
}

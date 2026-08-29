namespace SimplArchive.Infrastructure.Http;

/// <summary>
/// Thrown at the moment of connecting, when a caller-supplied URL resolves somewhere this installation may not
/// reach (ADR 0717). It surfaces as a delivery failure, which is what it is.
/// </summary>
public sealed class OutboundAddressRefusedException : Exception
{
    public OutboundAddressRefusedException(string host)
        : base($"The host {host} resolves to an address this installation may not call.")
    {
    }
}

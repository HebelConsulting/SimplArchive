using System.Net;
using System.Net.Sockets;
using SimplArchive.Application.Abstractions;

namespace SimplArchive.Infrastructure.Http;

/// <summary>
/// The message handler every outbound request to a caller-supplied URL goes through (ADR 0717). It does the
/// two things a registration-time check cannot: it resolves the name at the moment of connecting and
/// <b>connects to the address it just validated</b>, and it refuses to follow a redirect.
/// </summary>
public static class GuardedOutboundHandler
{
    public static SocketsHttpHandler Create(IOutboundAddressPolicy policy) => new()
    {
        // A redirect is a second target, chosen by the endpoint rather than by the administrator. Pinning the
        // first hop and then following a 302 to wherever it points would give away everything pinning bought,
        // so the redirect is a delivery failure instead of a second, less-scrutinised validation path.
        AllowAutoRedirect = false,

        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);

            // Every answer must be permitted, and the socket then connects to THESE addresses rather than to
            // the name — so nothing re-resolves between the check and the connection. That gap is the whole of
            // DNS rebinding: a name that answers publicly while it is being validated and privately a moment
            // later, when the connection is actually made.
            if (addresses.Length == 0 || !Array.TrueForAll(addresses, policy.IsPermitted))
            {
                throw new OutboundAddressRefusedException(host);
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();

                throw;
            }
        },
    };
}

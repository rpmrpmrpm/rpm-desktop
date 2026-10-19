using System.Net;
using System.Net.Sockets;

namespace RPM.Core.Tests;

/// <summary>
/// A listening HTTP server on a port nobody else has.
///
/// <para>The obvious way to find a free port, bind zero, read the number, close, hand the number to
/// the real server, has a gap in the middle: between the probe closing and the server opening, the
/// port belongs to nobody, and anything else asking the kernel for an ephemeral port can be given the
/// same one. This assembly runs its test classes in parallel and three of them stand up servers, so
/// that gap is reached often enough to matter: the loser gets an <see cref="HttpListenerException"/>
/// out of Start, and the test fails for a reason that has nothing to do with what it was testing.</para>
///
/// <para>The gap cannot be closed (a port cannot be held and handed over at once), so this takes the
/// other way out and asks again. A collision is rare and independent; a second draw from a range of
/// tens of thousands practically never hits the same one, and ten draws never have.</para>
/// </summary>
internal static class LoopbackHttp
{
    /// <summary>Starts a listener on a free loopback port. <paramref name="root"/> is its base URL,
    /// trailing slash and all, which is the form <see cref="HttpListener.Prefixes"/> requires.</summary>
    internal static HttpListener Start(out string root)
    {
        for (var attempt = 1; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var url = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(url);

            try
            {
                listener.Start();
                root = url;
                return listener;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                // Somebody took the port in the gap. Close this one and draw another number.
                listener.Close();
            }
        }
    }
}

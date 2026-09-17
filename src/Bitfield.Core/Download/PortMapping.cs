using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Bitfield.Core.Download;

/// <summary>
/// Asks the router to forward a port, over UPnP.
///
/// It matters more here than it looks. A client behind a router can reach out
/// but cannot be reached, and in a well seeded swarm the peers with something
/// to gain are the ones dialling out — so a client nobody can dial is one
/// nobody downloads from, however willing it is. On a tracker that keeps
/// ratios, that is the difference between contributing and not.
///
/// The exchange is two steps: a multicast search for a router that admits to
/// having an internet connection, and a SOAP call to it. Routers differ enough
/// that everything here is written to fail quietly — a mapping that cannot be
/// made is a client that works slightly less well, not one that stops.
/// </summary>
public static class PortMapping
{
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("239.255.255.250"), 1900);

    private static readonly string[] ServiceTypes =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    public sealed record Mapping(Uri Control, string ServiceType, int Port, IPAddress LocalAddress)
    {
        public override string ToString() => $"port {Port} forwarded to {LocalAddress}";
    }

    /// <summary>
    /// Finds a router and asks it to forward a port, returning what was done so
    /// that it can be undone on the way out. Null means no router answered, or
    /// it refused.
    /// </summary>
    public static async Task<Mapping?> AddAsync(
        int port,
        string description,
        CancellationToken cancellationToken = default)
    {
        foreach ((Uri location, string _) in await DiscoverAsync(cancellationToken).ConfigureAwait(false))
        {
            (Uri Control, string ServiceType)? found =
                await FindServiceAsync(location, cancellationToken).ConfigureAwait(false);

            if (found is not (Uri control, string serviceType))
            {
                continue;
            }

            IPAddress? local = LocalAddressFor(control.Host);
            if (local == null)
            {
                continue;
            }

            string body =
                $"<u:AddPortMapping xmlns:u=\"{serviceType}\">"
                + "<NewRemoteHost></NewRemoteHost>"
                + $"<NewExternalPort>{port}</NewExternalPort>"
                + "<NewProtocol>TCP</NewProtocol>"
                + $"<NewInternalPort>{port}</NewInternalPort>"
                + $"<NewInternalClient>{local}</NewInternalClient>"
                + "<NewEnabled>1</NewEnabled>"
                + $"<NewPortMappingDescription>{Escape(description)}</NewPortMappingDescription>"
                + "<NewLeaseDuration>0</NewLeaseDuration>"
                + "</u:AddPortMapping>";

            if (await SoapAsync(control, serviceType, "AddPortMapping", body, cancellationToken).ConfigureAwait(false))
            {
                return new Mapping(control, serviceType, port, local);
            }
        }

        return null;
    }

    /// <summary>
    /// Takes the mapping away again. Leaving one behind is untidy rather than
    /// harmful, but a router's table is small and a client that adds one per
    /// run eventually fills it.
    /// </summary>
    public static async Task RemoveAsync(Mapping mapping, CancellationToken cancellationToken = default)
    {
        string body =
            $"<u:DeletePortMapping xmlns:u=\"{mapping.ServiceType}\">"
            + "<NewRemoteHost></NewRemoteHost>"
            + $"<NewExternalPort>{mapping.Port}</NewExternalPort>"
            + "<NewProtocol>TCP</NewProtocol>"
            + "</u:DeletePortMapping>";

        await SoapAsync(mapping.Control, mapping.ServiceType, "DeletePortMapping", body, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The address this machine reaches a given host from.</summary>
    private static IPAddress? LocalAddressFor(string host)
    {
        try
        {
            using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect(host, 9);
            return (probe.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<(Uri Location, string Search)>> DiscoverAsync(
        CancellationToken cancellationToken)
    {
        List<(Uri, string)> found = [];

        foreach (string search in ServiceTypes)
        {
            string request =
                "M-SEARCH * HTTP/1.1\r\n"
                + "HOST: 239.255.255.250:1900\r\n"
                + "MAN: \"ssdp:discover\"\r\n"
                + "MX: 2\r\n"
                + $"ST: {search}\r\n\r\n";

            try
            {
                using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(new IPEndPoint(IPAddress.Any, 0));

                await socket.SendToAsync(Encoding.ASCII.GetBytes(request), SocketFlags.None, Multicast, cancellationToken)
                    .ConfigureAwait(false);

                using CancellationTokenSource listening =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                listening.CancelAfter(TimeSpan.FromSeconds(3));

                byte[] buffer = new byte[4096];

                while (true)
                {
                    SocketReceiveFromResult reply = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), listening.Token)
                        .ConfigureAwait(false);

                    string text = Encoding.ASCII.GetString(buffer, 0, reply.ReceivedBytes);
                    Match location = Regex.Match(text, @"LOCATION:\s*(\S+)", RegexOptions.IgnoreCase);

                    if (location.Success && Uri.TryCreate(location.Groups[1].Value, UriKind.Absolute, out Uri? uri)
                        && !found.Any(entry => entry.Item1 == uri))
                    {
                        found.Add((uri, search));
                    }
                }
            }
            catch (Exception)
            {
                // The three seconds are up, or there is no router to be found.
            }

            if (found.Count > 0)
            {
                break;
            }
        }

        return found;
    }

    /// <summary>
    /// Reads the router's device description and picks out the URL of a service
    /// that can forward a port.
    /// </summary>
    private static async Task<(Uri Control, string ServiceType)?> FindServiceAsync(
        Uri location,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(5) };
            string description = await http.GetStringAsync(location, cancellationToken).ConfigureAwait(false);

            foreach (string serviceType in ServiceTypes)
            {
                Match match = Regex.Match(
                    description,
                    $@"<serviceType>{Regex.Escape(serviceType)}</serviceType>.*?<controlURL>(.*?)</controlURL>",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase);

                if (match.Success && Uri.TryCreate(location, match.Groups[1].Value.Trim(), out Uri? control))
                {
                    return (control, serviceType);
                }
            }
        }
        catch (Exception)
        {
            // A router that will not describe itself is one to give up on.
        }

        return null;
    }

    private static async Task<bool> SoapAsync(
        Uri control,
        string serviceType,
        string action,
        string body,
        CancellationToken cancellationToken)
    {
        string envelope =
            "<?xml version=\"1.0\"?>"
            + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\""
            + " s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
            + $"<s:Body>{body}</s:Body></s:Envelope>";

        try
        {
            using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(8) };
            using StringContent content = new(envelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPACTION", $"\"{serviceType}#{action}\"");

            using HttpResponseMessage response = await http.PostAsync(control, content, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

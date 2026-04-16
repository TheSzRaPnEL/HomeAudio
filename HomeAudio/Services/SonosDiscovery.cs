using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using HomeAudio.Models;

namespace HomeAudio.Services;

/// <summary>
/// Discovers Sonos ZonePlayer devices on the local network via SSDP (UPnP multicast).
/// </summary>
public static class SonosDiscovery
{
    private const string MulticastAddress = "239.255.255.250";
    private const int    SsdpPort         = 1900;

    private static readonly string SearchMessage =
        "M-SEARCH * HTTP/1.1\r\n" +
        $"HOST: {MulticastAddress}:{SsdpPort}\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 3\r\n" +
        "ST: urn:schemas-upnp-org:device:ZonePlayer:1\r\n\r\n";

    /// <summary>
    /// Sends an SSDP M-SEARCH and collects responses for <paramref name="timeoutMs"/> ms.
    /// Returns one SonosDevice per discovered IP address.
    /// </summary>
    public static async Task<List<SonosDevice>> DiscoverAsync(int timeoutMs = 5000)
    {
        var found = new Dictionary<string, SonosDevice>(StringComparer.OrdinalIgnoreCase);
        var httpTasks = new List<Task>();

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        byte[] packet    = Encoding.UTF8.GetBytes(SearchMessage);
        var    multicast = new IPEndPoint(IPAddress.Parse(MulticastAddress), SsdpPort);
        await udp.SendAsync(packet, packet.Length, multicast);

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(cts.Token);
                }
                catch (OperationCanceledException) { break; }

                string response = Encoding.UTF8.GetString(result.Buffer);
                string? location = ExtractHeader(response, "LOCATION");
                if (location == null) continue;

                if (!Uri.TryCreate(location, UriKind.Absolute, out var uri)) continue;
                string ip   = uri.Host;
                int    port = uri.Port > 0 ? uri.Port : 1400;

                if (found.ContainsKey(ip)) continue;
                found[ip] = null!; // placeholder to avoid duplicate fetches

                // Fetch device description in parallel without blocking the receive loop
                string capturedLocation = location;
                string capturedIp       = ip;
                int    capturedPort     = port;
                httpTasks.Add(Task.Run(async () =>
                {
                    var device = await FetchDeviceDescriptionAsync(capturedLocation, capturedIp, capturedPort);
                    if (device != null)
                        lock (found) { found[capturedIp] = device; }
                }));
            }
        }
        catch (SocketException) { /* network error */ }

        // Wait for all HTTP fetches to complete
        await Task.WhenAll(httpTasks);

        return found.Values.Where(d => d != null).ToList();
    }

    private static async Task<SonosDevice?> FetchDeviceDescriptionAsync(string location, string ip, int port)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            string xml = await http.GetStringAsync(location);

            var doc = XDocument.Parse(xml);
            XNamespace upnp = "urn:schemas-upnp-org:device-1-0";
            var dev = doc.Root?.Element(upnp + "device");
            if (dev == null) return null;

            // Only accept ZonePlayer devices
            string? deviceType = dev.Element(upnp + "deviceType")?.Value ?? "";
            if (!deviceType.Contains("ZonePlayer", StringComparison.OrdinalIgnoreCase))
                return null;

            return new SonosDevice
            {
                Uuid      = dev.Element(upnp + "UDN")?.Value        ?? $"uuid:{ip}",
                Name      = dev.Element(upnp + "friendlyName")?.Value ?? ip,
                ModelName = dev.Element(upnp + "modelName")?.Value   ?? "Sonos",
                IpAddress = ip,
                Port      = port
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractHeader(string httpResponse, string headerName)
    {
        foreach (var raw in httpResponse.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                return line[(headerName.Length + 1)..].Trim();
        }
        return null;
    }
}

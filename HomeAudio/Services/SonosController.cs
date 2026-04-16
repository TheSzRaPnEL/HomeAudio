using System.Net.Http;
using System.Text;

namespace HomeAudio.Services;

/// <summary>
/// Sends UPnP/SOAP commands to a Sonos ZonePlayer.
/// All methods are fire-and-forget friendly — errors are swallowed so one
/// unresponsive speaker doesn't block the others.
/// </summary>
public sealed class SonosController : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

    // ── AVTransport service ──────────────────────────────────────────────────

    /// <summary>Tells the Sonos device to load a stream URL and start buffering.</summary>
    public Task SetAVTransportUriAsync(string ip, int port, string streamUrl, string trackTitle = "HomeAudio")
    {
        string protocol = streamUrl.Contains(".wav", StringComparison.OrdinalIgnoreCase)
            ? "http-get:*:audio/wav:*"
            : "http-get:*:audio/mpeg:*";

        // DIDL-Lite metadata required by Sonos to accept the URI
        string metadata = BuildDidlMetadata(streamUrl, trackTitle, protocol);

        return SoapAsync(ip, port,
            "/MediaRenderer/AVTransport/Control",
            "urn:schemas-upnp-org:service:AVTransport:1",
            "SetAVTransportURI",
            $"<InstanceID>0</InstanceID>" +
            $"<CurrentURI>{XmlEscape(streamUrl)}</CurrentURI>" +
            $"<CurrentURIMetaData>{XmlEscape(metadata)}</CurrentURIMetaData>");
    }

    public Task PlayAsync(string ip, int port) =>
        SoapAsync(ip, port,
            "/MediaRenderer/AVTransport/Control",
            "urn:schemas-upnp-org:service:AVTransport:1",
            "Play",
            "<InstanceID>0</InstanceID><Speed>1</Speed>");

    public Task PauseAsync(string ip, int port) =>
        SoapAsync(ip, port,
            "/MediaRenderer/AVTransport/Control",
            "urn:schemas-upnp-org:service:AVTransport:1",
            "Pause",
            "<InstanceID>0</InstanceID>");

    public Task StopAsync(string ip, int port) =>
        SoapAsync(ip, port,
            "/MediaRenderer/AVTransport/Control",
            "urn:schemas-upnp-org:service:AVTransport:1",
            "Stop",
            "<InstanceID>0</InstanceID>");

    // ── RenderingControl service ─────────────────────────────────────────────

    /// <summary>Sets volume on the device. <paramref name="volume"/> is 0–1 (maps to Sonos 0–100).</summary>
    public Task SetVolumeAsync(string ip, int port, float volume)
    {
        int v = Math.Clamp((int)(volume * 100), 0, 100);
        return SoapAsync(ip, port,
            "/MediaRenderer/RenderingControl/Control",
            "urn:schemas-upnp-org:service:RenderingControl:1",
            "SetVolume",
            $"<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>{v}</DesiredVolume>");
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private async Task SoapAsync(string ip, int port, string path, string service, string action, string bodyContent)
    {
        string envelope =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{service}">
                  {bodyContent}
                </u:{action}>
              </s:Body>
            </s:Envelope>
            """;

        var content = new StringContent(envelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{service}#{action}\"");

        try
        {
            await _http.PostAsync($"http://{ip}:{port}{path}", content);
        }
        catch
        {
            // Swallow — caller decides whether to surface the error
        }
    }

    private static string BuildDidlMetadata(string uri, string title, string protocol) =>
        $"<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        $"xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" " +
        $"xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">" +
        $"<item id=\"1\" parentID=\"0\" restricted=\"true\">" +
        $"<dc:title>{XmlEscape(title)}</dc:title>" +
        $"<upnp:class>object.item.audioItem.musicTrack</upnp:class>" +
        $"<res protocolInfo=\"{protocol}\">{XmlEscape(uri)}</res>" +
        $"</item></DIDL-Lite>";

    private static string XmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    public void Dispose() => _http.Dispose();
}

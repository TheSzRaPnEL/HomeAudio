using System.Net;
using System.Net.Sockets;
using System.Text;
using HomeAudio.Models;
using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Minimal HTTP server built on raw TcpListener — bypasses Windows HTTP.sys
/// entirely, so no URL ACL reservation or elevated privileges are needed.
///
/// Serves pre-decoded audio as WAV files at:
///   GET /audio/stereo.wav   — full stereo mix
///   GET /audio/left.wav     — left channel mono
///   GET /audio/right.wav    — right channel mono
/// </summary>
public sealed class AudioStreamServer : IDisposable
{
    private TcpListener?              _tcp;
    private CancellationTokenSource?  _cts;
    private string _session = NewSession();

    private byte[]? _stereoWav;
    private byte[]? _leftWav;
    private byte[]? _rightWav;

    public string   LocalIp  { get; private set; } = "127.0.0.1";
    public int      Port     { get; private set; }
    public TimeSpan Duration { get; private set; }

    // ── Prepare ──────────────────────────────────────────────────────────────

    public void PrepareStreams(float[] samples, WaveFormat srcFormat)
    {
        _stereoWav = BuildWav(samples, srcFormat, DeviceChannel.Stereo);
        _leftWav   = srcFormat.Channels >= 2
            ? BuildWav(samples, srcFormat, DeviceChannel.LeftOnly)  : _stereoWav;
        _rightWav  = srcFormat.Channels >= 2
            ? BuildWav(samples, srcFormat, DeviceChannel.RightOnly) : _stereoWav;

        // Compute duration from sample count so DIDL metadata can include it
        int ch = Math.Max(1, srcFormat.Channels);
        Duration = TimeSpan.FromSeconds((double)samples.Length / srcFormat.SampleRate / ch);
    }

    /// <summary>Returns the byte length of the pre-built WAV for the given channel.</summary>
    public long GetStreamSize(DeviceChannel channel) => channel switch
    {
        DeviceChannel.LeftOnly  => _leftWav?.LongLength  ?? 0,
        DeviceChannel.RightOnly => _rightWav?.LongLength ?? 0,
        _                       => _stereoWav?.LongLength ?? 0
    };

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        Stop();
        LocalIp  = GetLocalNetworkIp();
        Port     = GetFreePort();
        _session = NewSession();
        _cts     = new CancellationTokenSource();

        // Bind to all interfaces so Sonos on the LAN can connect.
        // TcpListener operates at raw TCP level — no HTTP.sys, no URL ACL, no admin needed.
        _tcp = new TcpListener(IPAddress.Any, Port);
        _tcp.Start();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _tcp?.Stop(); } catch { /* already stopped */ }
        _tcp = null;
    }

    public string GetStreamUrl(DeviceChannel channel)
    {
        string file = channel switch
        {
            DeviceChannel.LeftOnly  => "left",
            DeviceChannel.RightOnly => "right",
            _                       => "stereo"
        };
        return $"http://{LocalIp}:{Port}/audio/{file}.wav?s={_session}";
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp!.AcceptTcpClientAsync(ct);
            }
            catch { break; }

            _ = Task.Run(() => ServeClientAsync(client), ct);
        }
    }

    // ── Per-connection handler ────────────────────────────────────────────────

    private async Task ServeClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout    = 30_000;

                var stream = client.GetStream();

                // Read the HTTP request — we only need the first line
                string requestLine = await ReadRequestLineAsync(stream);

                // Parse:  GET /audio/stereo.wav?s=abc HTTP/1.1
                var parts = requestLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string path   = parts[1].Split('?')[0].ToLowerInvariant();

                byte[]? data = null;
                if      (path.EndsWith("stereo.wav")) data = _stereoWav;
                else if (path.EndsWith("left.wav"))   data = _leftWav;
                else if (path.EndsWith("right.wav"))  data = _rightWav;

                if (data == null)
                {
                    await WriteResponseAsync(stream, 404, "Not Found", null, method);
                    return;
                }

                await WriteResponseAsync(stream, 200, "OK", data, method);
            }
            catch { /* client disconnected or timed out */ }
        }
    }

    private static async Task<string> ReadRequestLineAsync(NetworkStream stream)
    {
        var sb  = new StringBuilder(256);
        var buf = new byte[1];

        // Read byte-by-byte until \r\n (the end of the first HTTP request line)
        char prev = '\0';
        while (true)
        {
            int n = await stream.ReadAsync(buf, 0, 1);
            if (n == 0) break;

            char c = (char)buf[0];
            if (prev == '\r' && c == '\n') break;
            if (c != '\r') sb.Append(c);
            prev = c;
        }
        return sb.ToString();
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int statusCode, string statusText,
        byte[]? body, string method)
    {
        int length = body?.Length ?? 0;
        string header =
            $"HTTP/1.1 {statusCode} {statusText}\r\n" +
            $"Content-Type: audio/wav\r\n" +
            $"Content-Length: {length}\r\n" +
            "Accept-Ranges: bytes\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n" +
            "\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));

        // HEAD requests get headers only
        if (method != "HEAD" && body != null)
            await stream.WriteAsync(body);

        await stream.FlushAsync();
    }

    // ── WAV builder ───────────────────────────────────────────────────────────

    private static byte[] BuildWav(float[] floatSamples, WaveFormat srcFmt, DeviceChannel channel)
    {
        int srcChannels = srcFmt.Channels;
        int sampleRate  = srcFmt.SampleRate;
        int outChannels;
        short[] pcm;

        if (channel == DeviceChannel.Stereo || srcChannels == 1)
        {
            outChannels = srcChannels;
            pcm = new short[floatSamples.Length];
            for (int i = 0; i < floatSamples.Length; i++)
                pcm[i] = FloatToShort(floatSamples[i]);
        }
        else
        {
            int chIdx   = channel == DeviceChannel.LeftOnly ? 0 : 1;
            outChannels = 1;
            int frames  = floatSamples.Length / srcChannels;
            pcm = new short[frames];
            for (int i = 0; i < frames; i++)
                pcm[i] = FloatToShort(floatSamples[i * srcChannels + chIdx]);
        }

        int dataSize   = pcm.Length * 2;
        int byteRate   = sampleRate * outChannels * 2;
        int blockAlign = outChannels * 2;

        using var ms = new System.IO.MemoryStream(44 + dataSize);
        using var w  = new System.IO.BinaryWriter(ms);

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataSize);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)outChannels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)blockAlign);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataSize);
        foreach (short s in pcm) w.Write(s);

        return ms.ToArray();
    }

    private static short FloatToShort(float f)
        => (short)Math.Clamp((int)(f * 32767f), short.MinValue, short.MaxValue);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetLocalNetworkIp()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 53);
            return ((IPEndPoint)s.LocalEndPoint!).Address.ToString();
        }
        catch { }

        foreach (var addr in Dns.GetHostAddresses(Dns.GetHostName()))
            if (addr.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr))
                return addr.ToString();

        return "127.0.0.1";
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string NewSession() => Guid.NewGuid().ToString("N")[..8];

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

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

    // Live-streaming state for mic passthrough to Sonos
    private MicrophoneCapture?      _liveMic;
    private WaveFormat?             _liveFormat;
    private CancellationTokenSource _liveStreamCts = new();

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

    // ── Live streaming (mic passthrough) ─────────────────────────────────────

    /// <summary>
    /// Puts the server into live-streaming mode. Connections to /audio/live-*.wav
    /// will stream real-time PCM from <paramref name="capture"/> until
    /// <see cref="StopLiveStream"/> is called.
    /// </summary>
    public void StartLiveStream(MicrophoneCapture capture, WaveFormat format)
    {
        _liveStreamCts.Cancel();
        _liveStreamCts = new CancellationTokenSource();
        _liveMic    = capture;
        _liveFormat = format;
    }

    public void StopLiveStream()
    {
        _liveStreamCts.Cancel();
        _liveMic    = null;
        _liveFormat = null;
    }

    public string GetLiveStreamUrl(DeviceChannel channel)
    {
        string file = channel switch
        {
            DeviceChannel.LeftOnly  => "live-left",
            DeviceChannel.RightOnly => "live-right",
            _                       => "live-stereo"
        };
        return $"http://{LocalIp}:{Port}/audio/{file}.wav?s={_session}";
    }

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

                // Read ALL headers up to \r\n\r\n so the receive buffer is empty
                // before we close the socket.  If we close with unread data in the
                // receive buffer, Windows TCP sends RST instead of FIN — this aborts
                // the transfer before Sonos finishes reading the WAV body.
                var (method, path, rangeStart, rangeEnd) = await ReadHttpRequestAsync(stream);

                // Live stream endpoints (mic passthrough to Sonos)
                if (path.StartsWith("/audio/live-"))
                {
                    DeviceChannel liveCh = path.StartsWith("/audio/live-left")  ? DeviceChannel.LeftOnly
                                         : path.StartsWith("/audio/live-right") ? DeviceChannel.RightOnly
                                         : DeviceChannel.Stereo;
                    client.SendTimeout = 0;   // no timeout — stream stays open until mic stops
                    await ServeLiveClientAsync(stream, liveCh, method);
                    return;
                }

                byte[]? data = null;
                if      (path.EndsWith("stereo.wav")) data = _stereoWav;
                else if (path.EndsWith("left.wav"))   data = _leftWav;
                else if (path.EndsWith("right.wav"))  data = _rightWav;

                if (data == null)
                {
                    await WriteResponseAsync(stream, 404, "Not Found", null, 0, 0, method);
                    return;
                }

                long fileSize = data.LongLength;
                long start    = rangeStart ?? 0;
                long end      = rangeEnd   ?? (fileSize - 1);
                start = Math.Max(0, Math.Min(start, fileSize - 1));
                end   = Math.Max(start, Math.Min(end, fileSize - 1));

                bool isRange = rangeStart.HasValue;
                await WriteResponseAsync(
                    stream,
                    isRange ? 206 : 200,
                    isRange ? "Partial Content" : "OK",
                    data, start, end, method);
            }
            catch { /* client disconnected or timed out */ }
        }
    }

    /// <summary>
    /// Reads all HTTP request headers up to the blank line that terminates them.
    /// Also extracts the method, path, and optional Range header.
    /// Draining all headers ensures no unread data remains in the socket receive
    /// buffer, allowing a graceful TCP FIN rather than a RST on close.
    /// </summary>
    private static async Task<(string method, string path, long? rangeStart, long? rangeEnd)>
        ReadHttpRequestAsync(NetworkStream stream)
    {
        var  sb        = new StringBuilder(256);
        var  buf       = new byte[1];
        char prev      = '\0';
        bool firstLine = true;
        string method  = "GET";
        string path    = "/";
        long?  rangeStart = null;
        long?  rangeEnd   = null;

        while (true)
        {
            int n = await stream.ReadAsync(buf, 0, 1);
            if (n == 0) break;

            char c = (char)buf[0];

            if (prev == '\r' && c == '\n')
            {
                string line = sb.ToString();
                sb.Clear();

                if (firstLine)
                {
                    // "GET /audio/stereo.wav?s=abc HTTP/1.1"
                    var parts = line.Split(' ');
                    if (parts.Length >= 2)
                    {
                        method = parts[0].ToUpperInvariant();
                        path   = parts[1].Split('?')[0].ToLowerInvariant();
                    }
                    firstLine = false;
                }
                else if (line.Length == 0)
                {
                    break; // blank line = end of headers
                }
                else
                {
                    // Parse Range header only; skip everything else
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        string hName  = line[..colon].Trim();
                        string hValue = line[(colon + 1)..].Trim();
                        if (hName.Equals("Range", StringComparison.OrdinalIgnoreCase) &&
                            hValue.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            // e.g. "bytes=0-"  "bytes=0-999"  "bytes=-500"
                            string range = hValue[6..];
                            int    dash  = range.IndexOf('-');
                            if (dash >= 0)
                            {
                                if (dash > 0 && long.TryParse(range[..dash], out long rs))
                                    rangeStart = rs;
                                if (dash < range.Length - 1 && long.TryParse(range[(dash + 1)..], out long re))
                                    rangeEnd = re;
                            }
                        }
                    }
                }
            }
            else if (c != '\r')
            {
                sb.Append(c);
            }

            prev = c;
        }

        return (method, path, rangeStart, rangeEnd);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream, int statusCode, string statusText,
        byte[]? data, long rangeStart, long rangeEnd, string method)
    {
        long bodyLength = data != null ? (rangeEnd - rangeStart + 1) : 0;
        long fileSize   = data?.LongLength ?? 0;

        var header = new StringBuilder();
        header.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        header.Append( "Content-Type: audio/wav\r\n");
        header.Append($"Content-Length: {bodyLength}\r\n");
        header.Append( "Accept-Ranges: bytes\r\n");
        if (statusCode == 206)
            header.Append($"Content-Range: bytes {rangeStart}-{rangeEnd}/{fileSize}\r\n");
        header.Append("Cache-Control: no-cache\r\n");
        header.Append("Connection: close\r\n");
        header.Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()));

        // HEAD requests get headers only
        if (method != "HEAD" && data != null)
            await stream.WriteAsync(data, (int)rangeStart, (int)bodyLength);

        await stream.FlushAsync();
    }

    // ── Live-stream handler ───────────────────────────────────────────────────

    /// <summary>
    /// Serves a never-ending WAV stream to a Sonos client. The WAV header declares
    /// a large file size; raw PCM bytes are pumped from the mic ring buffer at
    /// real-time pace until the live stream is stopped or the client disconnects.
    /// </summary>
    private async Task ServeLiveClientAsync(NetworkStream netStream, DeviceChannel channel, string method)
    {
        if (_liveMic == null || _liveFormat == null)
        {
            await WriteResponseAsync(netStream, 404, "Not Found", null, 0, 0, method);
            return;
        }

        var ct         = _liveStreamCts.Token;
        int sampleRate  = _liveFormat.SampleRate;
        int srcChannels = _liveFormat.Channels;
        int outChannels = channel == DeviceChannel.Stereo ? srcChannels : 1;
        int chIdx       = channel == DeviceChannel.RightOnly ? 1 : 0;
        int byteRate    = sampleRate * outChannels * 2;

        // Declare a large WAV so Sonos accepts the stream immediately.
        const int MaxWavBytes = 0x7FFFFFFF;  // ~2 GB

        // Build the 44-byte WAV header
        var wavHeader = new byte[44];
        using (var ms = new System.IO.MemoryStream(wavHeader))
        using (var bw = new System.IO.BinaryWriter(ms))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(MaxWavBytes - 8);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1);                    // PCM
            bw.Write((short)outChannels);
            bw.Write(sampleRate);
            bw.Write(byteRate);
            bw.Write((short)(outChannels * 2));    // block align
            bw.Write((short)16);                   // bits per sample
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(MaxWavBytes - 44);
        }

        var httpHeaderBytes = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\n" +
            $"Content-Type: audio/wav\r\n" +
            $"Content-Length: {MaxWavBytes}\r\n" +
            $"Cache-Control: no-cache\r\n" +
            $"Connection: close\r\n" +
            $"\r\n");

        try
        {
            await netStream.WriteAsync(httpHeaderBytes, ct);
            if (method == "HEAD") return;
            await netStream.WriteAsync(wavHeader, ct);

            // Pump live audio at real-time rate to avoid flooding Sonos's buffer.
            var reader         = _liveMic.CreateReader();
            int framesPerChunk = sampleRate / 50;                           // 20 ms
            var floatBuf       = new float[framesPerChunk * srcChannels];
            var pcmBuf         = new byte [framesPerChunk * outChannels * 2];

            var  sw            = System.Diagnostics.Stopwatch.StartNew();
            long totalPcmBytes = 0;

            while (!ct.IsCancellationRequested)
            {
                // Rate-limit to real time so Sonos's buffer doesn't overfill.
                long expectedBytes = (long)(sw.Elapsed.TotalSeconds * byteRate);
                if (totalPcmBytes >= expectedBytes)
                {
                    await Task.Delay(framesPerChunk / (sampleRate / 1000) / 2, ct);
                    continue;
                }

                reader.Read(floatBuf, 0, floatBuf.Length);

                int pcmCount = 0;
                if (channel == DeviceChannel.Stereo || srcChannels == 1)
                {
                    for (int i = 0; i < floatBuf.Length; i++)
                    {
                        short s = FloatToShort(floatBuf[i]);
                        pcmBuf[pcmCount++] = (byte)(s        & 0xFF);
                        pcmBuf[pcmCount++] = (byte)((s >> 8) & 0xFF);
                    }
                }
                else
                {
                    // Extract one channel from interleaved stereo
                    for (int i = chIdx; i < floatBuf.Length; i += srcChannels)
                    {
                        short s = FloatToShort(floatBuf[i]);
                        pcmBuf[pcmCount++] = (byte)(s        & 0xFF);
                        pcmBuf[pcmCount++] = (byte)((s >> 8) & 0xFF);
                    }
                }

                await netStream.WriteAsync(pcmBuf, 0, pcmCount, ct);
                totalPcmBytes += pcmCount;
            }
        }
        catch (OperationCanceledException) { /* mic stopped — expected */ }
        catch { /* client disconnected */ }
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
        StopLiveStream();
        Stop();
        GC.SuppressFinalize(this);
    }
}

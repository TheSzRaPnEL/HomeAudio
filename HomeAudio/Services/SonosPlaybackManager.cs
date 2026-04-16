using HomeAudio.Models;
using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Coordinates audio streaming + UPnP control for all active Sonos devices.
///
/// Playback flow:
///   1. PrepareAsync()  — builds WAV streams, starts HTTP server, loads URIs into devices
///   2. PlayAsync()     — triggers Play on each device (with optional latency delay)
///   3. PauseAsync() / StopAsync() as needed
///
/// Latency handling:
///   Each SonosDevice.LatencyOffsetMs determines how long after the WaveOut
///   engine starts we call Play() on that Sonos speaker.  The caller (MainViewModel)
///   is responsible for inserting corresponding silence padding into the WaveOut
///   pipeline for devices that should start earlier than the slowest Sonos device.
/// </summary>
public sealed class SonosPlaybackManager : IDisposable
{
    private readonly SonosController  _ctrl   = new();
    private readonly AudioStreamServer _server = new();
    private List<SonosDevice> _active = new();

    // ── Discovery ────────────────────────────────────────────────────────────

    public Task<List<SonosDevice>> DiscoverAsync(int timeoutMs = 5000)
        => SonosDiscovery.DiscoverAsync(timeoutMs);

    // ── Prepare / Play / Control ─────────────────────────────────────────────

    /// <summary>
    /// Builds channel-specific WAV streams and loads transport URIs into each device.
    /// Must be called before <see cref="PlayAsync"/>.
    /// </summary>
    public async Task PrepareAsync(
        float[]          samples,
        WaveFormat       format,
        IEnumerable<SonosDevice> devices,
        SonosStereoPair? stereoPair)
    {
        _active = devices.Where(d => d.IsActive).ToList();
        if (_active.Count == 0) return;

        // Build WAV byte arrays
        _server.PrepareStreams(samples, format);
        _server.Start();

        var tasks = new List<Task>();
        foreach (var device in _active)
        {
            // Determine channel (stereo pair overrides device setting)
            DeviceChannel ch = device.Channel;
            if (stereoPair is { IsEnabled: true, IsValid: true })
            {
                if (device == stereoPair.LeftDevice)  ch = DeviceChannel.LeftOnly;
                if (device == stereoPair.RightDevice) ch = DeviceChannel.RightOnly;
            }

            device.StreamUrl = _server.GetStreamUrl(ch);

            SonosDevice captured = device;
            tasks.Add(Task.Run(async () =>
            {
                await _ctrl.SetVolumeAsync(captured.IpAddress, captured.Port, captured.Volume);
                await _ctrl.SetAVTransportUriAsync(captured.IpAddress, captured.Port,
                    captured.StreamUrl, "HomeAudio");
            }));
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Starts playback on all prepared devices, honouring per-device latency offsets.
    /// <paramref name="referenceMs"/> is the number of milliseconds after which WaveOut
    /// will begin producing audible audio (i.e. the silence-padding duration of the
    /// fastest WaveOut device).  Sonos Play() is called at the equivalent wall-clock time
    /// so both engines produce audio simultaneously.
    /// </summary>
    public async Task PlayAsync(int referenceMs = 0)
    {
        if (_active.Count == 0) return;

        var tasks = _active.Select(device => Task.Run(async () =>
        {
            int delayMs = Math.Max(0, device.LatencyOffsetMs - referenceMs);
            if (delayMs > 0)
                await Task.Delay(delayMs);
            await _ctrl.PlayAsync(device.IpAddress, device.Port);
        }));

        await Task.WhenAll(tasks);
    }

    public async Task PauseAsync()
    {
        var tasks = _active.Select(d => _ctrl.PauseAsync(d.IpAddress, d.Port));
        await Task.WhenAll(tasks);
    }

    public async Task StopAsync()
    {
        var tasks = _active.Select(d => _ctrl.StopAsync(d.IpAddress, d.Port));
        await Task.WhenAll(tasks);
        _server.Stop();
        _active.Clear();
    }

    /// <summary>Pushes a live volume change to a single device during playback.</summary>
    public Task UpdateVolumeAsync(SonosDevice device)
        => _ctrl.SetVolumeAsync(device.IpAddress, device.Port, device.Volume);

    public bool HasActiveDevices => _active.Count > 0;

    public void Dispose()
    {
        _ctrl.Dispose();
        _server.Dispose();
        GC.SuppressFinalize(this);
    }
}

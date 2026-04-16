using HomeAudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HomeAudio.Services;

/// <summary>
/// Orchestrates real-time microphone passthrough to one or more WaveOut devices
/// and/or Sonos speakers.
///
/// WaveOut path: captures from WASAPI, fans live audio to per-device players
///   using the same latency-compensation pipeline as file playback.
///
/// Sonos path: serves a live WAV stream over HTTP (<see cref="AudioStreamServer"/>)
///   and uses UPnP to tell each Sonos speaker to play from that stream URL.
/// </summary>
public class MicPassthroughEngine : IDisposable
{
    private readonly MicrophoneCapture _capture     = new();
    private readonly List<DevicePlayer> _players    = new();
    private readonly AudioStreamServer  _liveServer = new();
    private readonly SonosController    _sonosCtrl  = new();
    private readonly List<SonosDevice>  _sonosActive = new();

    public bool IsActive { get; private set; }

    public event Action<string>? Error;

    // ──────────────────────────────────────────────────────────────
    // Start / Stop
    // ──────────────────────────────────────────────────────────────

    public void Start(
        InputDevice              micDevice,
        IEnumerable<AudioDevice> outputDevices,
        StereoPair?              stereoPair,
        IEnumerable<SonosDevice>? sonosDevices = null,
        SonosStereoPair?          sonosPair    = null)
    {
        Stop();

        var waveDevices = outputDevices.ToList();
        var sonosList   = (sonosDevices ?? Enumerable.Empty<SonosDevice>()).ToList();

        if (waveDevices.Count == 0 && sonosList.Count == 0) return;

        // Start capture so the ring buffer begins filling
        _capture.Start(micDevice);

        // ── WaveOut devices ──────────────────────────────────────
        if (waveDevices.Count > 0)
        {
            int normalisedShift = NormaliseLatencyOffsets(waveDevices);

            foreach (var device in waveDevices)
            {
                DeviceChannel channel = device.Channel;

                if (stereoPair is { IsEnabled: true, IsValid: true })
                {
                    if (device == stereoPair.LeftDevice)  channel = DeviceChannel.LeftOnly;
                    if (device == stereoPair.RightDevice) channel = DeviceChannel.RightOnly;
                }

                int silenceMs = Math.Max(0, device.LatencyOffsetMs - normalisedShift);

                MicSampleProvider reader = _capture.CreateReader();
                var player = new DevicePlayer(device, reader, channel, silenceMs);
                player.PlaybackError += msg => Error?.Invoke(msg);
                _players.Add(player);
            }

            foreach (var p in _players) p.Prepare();
            foreach (var p in _players) p.Start();
        }

        // ── Sonos devices ────────────────────────────────────────
        if (sonosList.Count > 0)
        {
            _sonosActive.AddRange(sonosList);

            _liveServer.StartLiveStream(_capture, _capture.WaveFormat);
            _liveServer.Start();

            _ = Task.Run(async () =>
            {
                try
                {
                    // Load stream URI into each Sonos device
                    var setupTasks = _sonosActive.Select(device => Task.Run(async () =>
                    {
                        DeviceChannel ch = device.Channel;
                        if (sonosPair is { IsEnabled: true, IsValid: true })
                        {
                            if (device == sonosPair.LeftDevice)  ch = DeviceChannel.LeftOnly;
                            if (device == sonosPair.RightDevice) ch = DeviceChannel.RightOnly;
                        }
                        string url = _liveServer.GetLiveStreamUrl(ch);
                        await _sonosCtrl.SetVolumeAsync(device.IpAddress, device.Port, device.Volume);
                        await _sonosCtrl.SetAVTransportUriAsync(device.IpAddress, device.Port, url, "HomeAudio Mic");
                    }));
                    await Task.WhenAll(setupTasks);

                    // Start playback with per-device latency offset
                    int minOffset = _sonosActive.Count > 0 ? _sonosActive.Min(d => d.LatencyOffsetMs) : 0;
                    if (minOffset < 0)
                        foreach (var d in _sonosActive) d.LatencyOffsetMs -= minOffset;

                    var playTasks = _sonosActive.Select(device => Task.Run(async () =>
                    {
                        int delayMs = Math.Max(0, device.LatencyOffsetMs);
                        if (delayMs > 0) await Task.Delay(delayMs);
                        await _sonosCtrl.PlayAsync(device.IpAddress, device.Port);
                    }));
                    await Task.WhenAll(playTasks);
                }
                catch (Exception ex)
                {
                    Error?.Invoke(ex.Message);
                }
            });
        }

        IsActive = true;
    }

    public void Stop()
    {
        // WaveOut players
        foreach (var p in _players) p.Stop();
        DisposeDevicePlayers();

        // Sonos devices (fire-and-forget stop, best-effort)
        if (_sonosActive.Count > 0)
        {
            var devices = _sonosActive.ToList();
            _sonosActive.Clear();
            _ = Task.Run(() => Task.WhenAll(devices.Select(d =>
                _sonosCtrl.StopAsync(d.IpAddress, d.Port))));
            _liveServer.StopLiveStream();
            _liveServer.Stop();
        }

        _capture.Stop();
        IsActive = false;
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private static int NormaliseLatencyOffsets(IEnumerable<AudioDevice> devices)
    {
        var list = devices.ToList();
        if (list.Count == 0) return 0;
        int min = list.Min(d => d.LatencyOffsetMs);
        if (min >= 0) return 0;
        foreach (var d in list) d.LatencyOffsetMs -= min;
        return min;
    }

    private void DisposeDevicePlayers()
    {
        foreach (var p in _players) p.Dispose();
        _players.Clear();
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
        _liveServer.Dispose();
        _sonosCtrl.Dispose();
        GC.SuppressFinalize(this);
    }
}

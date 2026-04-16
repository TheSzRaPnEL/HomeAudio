using HomeAudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HomeAudio.Services;

/// <summary>
/// Orchestrates real-time microphone passthrough to one or more WaveOut devices.
/// Captures from a selected input device via WASAPI, then fans the live audio out
/// to each active output device using the same latency-compensation pipeline as
/// file playback (silence-padding per device, optional stereo-pair channel split).
/// </summary>
public class MicPassthroughEngine : IDisposable
{
    private readonly MicrophoneCapture _capture = new();
    private readonly List<DevicePlayer> _players = new();

    public bool IsActive { get; private set; }

    public event Action<string>? Error;

    // ──────────────────────────────────────────────────────────────
    // Start / Stop
    // ──────────────────────────────────────────────────────────────

    public void Start(
        InputDevice            micDevice,
        IEnumerable<AudioDevice> outputDevices,
        StereoPair?            stereoPair)
    {
        Stop();

        var devices = outputDevices.ToList();
        if (devices.Count == 0) return;

        // Start capture so the ring buffer begins filling
        _capture.Start(micDevice);

        // Normalise latency offsets (same logic as AudioEngine)
        int normalisedShift = NormaliseLatencyOffsets(devices);

        foreach (var device in devices)
        {
            DeviceChannel channel = device.Channel;

            if (stereoPair is { IsEnabled: true, IsValid: true })
            {
                if (device == stereoPair.LeftDevice)  channel = DeviceChannel.LeftOnly;
                if (device == stereoPair.RightDevice) channel = DeviceChannel.RightOnly;
            }

            int silenceMs = Math.Max(0, device.LatencyOffsetMs - normalisedShift);

            // Each device gets its own reader anchored at the current ring write pos.
            MicSampleProvider reader = _capture.CreateReader();

            var player = new DevicePlayer(device, reader, channel, silenceMs);
            player.PlaybackError += msg => Error?.Invoke(msg);
            _players.Add(player);
        }

        foreach (var p in _players) p.Prepare();
        foreach (var p in _players) p.Start();

        IsActive = true;
    }

    public void Stop()
    {
        foreach (var p in _players) p.Stop();
        DisposeDevicePlayers();

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
        GC.SuppressFinalize(this);
    }
}

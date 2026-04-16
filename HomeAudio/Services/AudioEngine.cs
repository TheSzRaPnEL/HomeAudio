using HomeAudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HomeAudio.Services;

/// <summary>
/// Core audio engine. Manages simultaneous playback across multiple output devices
/// with optional stereo-pair channel splitting and per-device volume/latency control.
/// </summary>
public class AudioEngine : IDisposable
{
    private readonly List<DevicePlayer> _players = new();
    private System.Timers.Timer? _positionTimer;

    public TimeSpan Duration { get; private set; }
    public TimeSpan Position => _players.Count > 0 ? _players[0].Position : TimeSpan.Zero;
    public AppPlaybackState State { get; private set; } = AppPlaybackState.Stopped;
    public string? LoadedFile { get; private set; }

    public event Action? PlaybackStopped;
    public event Action<string>? PlaybackError;
    public event Action<TimeSpan>? PositionChanged;

    // ──────────────────────────────────────────────────────────────
    // Load
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads an audio file and sets up playback for each active device.
    /// </summary>
    public void Load(string filePath, IEnumerable<AudioDevice> activeDevices, StereoPair? stereoPair)
    {
        Stop();
        DisposeDevicePlayers();

        LoadedFile = filePath;

        // Pre-decode the audio file into memory once, as float samples.
        float[] allSamples;
        WaveFormat sourceFormat;
        using (var reader = new AudioFileReader(filePath))
        {
            Duration = reader.TotalTime;
            sourceFormat = reader.WaveFormat;
            var sampleList = new List<float>();
            var buf = new float[reader.WaveFormat.SampleRate * reader.WaveFormat.Channels]; // 1s buffer
            int read;
            while ((read = reader.Read(buf, 0, buf.Length)) > 0)
                sampleList.AddRange(buf.Take(read));
            allSamples = sampleList.ToArray();
        }

        int normalisePositiveOffset = NormaliseLatencyOffsets(activeDevices);

        foreach (var device in activeDevices)
        {
            DeviceChannel channel = device.Channel;

            // If this device is part of a stereo pair, override channel
            if (stereoPair is { IsEnabled: true, IsValid: true })
            {
                if (device == stereoPair.LeftDevice)  channel = DeviceChannel.LeftOnly;
                if (device == stereoPair.RightDevice) channel = DeviceChannel.RightOnly;
            }

            int silenceMs = Math.Max(0, device.LatencyOffsetMs - normalisePositiveOffset);

            var player = new DevicePlayer(device, allSamples, sourceFormat, channel, silenceMs);
            player.PlaybackStopped += OnAnyPlayerStopped;
            player.PlaybackError   += msg => PlaybackError?.Invoke(msg);
            _players.Add(player);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Transport
    // ──────────────────────────────────────────────────────────────

    public void Play()
    {
        if (_players.Count == 0) return;

        bool resuming = State == AppPlaybackState.Paused;

        // Set state BEFORE starting so PlaybackStopped callbacks see the correct state
        State = AppPlaybackState.Playing;

        if (resuming)
        {
            foreach (var p in _players) p.Resume();
        }
        else
        {
            // Pre-init all players so Start() is almost instantaneous
            foreach (var p in _players) p.Prepare();
            foreach (var p in _players) p.Start();
        }

        StartPositionTimer();
    }

    public void Pause()
    {
        if (State != AppPlaybackState.Playing) return;
        foreach (var p in _players) p.Pause();
        State = AppPlaybackState.Paused;
        StopPositionTimer();
    }

    public void Stop()
    {
        foreach (var p in _players) p.Stop();
        State = AppPlaybackState.Stopped;
        StopPositionTimer();
    }

    public void Seek(TimeSpan position)
    {
        bool wasPlaying = State == AppPlaybackState.Playing;
        if (wasPlaying) foreach (var p in _players) p.Pause();

        foreach (var p in _players) p.Seek(position);

        if (wasPlaying) foreach (var p in _players) p.Resume();
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Normalises latency offsets so the minimum becomes 0.
    /// Returns the value subtracted from each offset so callers can apply it.
    /// </summary>
    private static int NormaliseLatencyOffsets(IEnumerable<AudioDevice> devices)
    {
        var offsets = devices.Select(d => d.LatencyOffsetMs).ToList();
        if (offsets.Count == 0) return 0;
        int minOffset = offsets.Min();
        if (minOffset >= 0) return 0;
        // shift all up so the device with most negative offset starts at 0
        foreach (var d in devices) d.LatencyOffsetMs -= minOffset;
        return minOffset;
    }

    private void OnAnyPlayerStopped()
    {
        // If the first player finishes, treat playback as done
        bool allStopped = _players.All(p => !p.IsPlaying);
        if (allStopped && State == AppPlaybackState.Playing)
        {
            State = AppPlaybackState.Stopped;
            StopPositionTimer();
            PlaybackStopped?.Invoke();
        }
    }

    private void StartPositionTimer()
    {
        _positionTimer = new System.Timers.Timer(250);
        _positionTimer.Elapsed += (_, _) => PositionChanged?.Invoke(Position);
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Stop();
        _positionTimer?.Dispose();
        _positionTimer = null;
    }

    private void DisposeDevicePlayers()
    {
        foreach (var p in _players) p.Dispose();
        _players.Clear();
    }

    public void Dispose()
    {
        Stop();
        DisposeDevicePlayers();
        _positionTimer?.Dispose();
        GC.SuppressFinalize(this);
    }
}

// ──────────────────────────────────────────────────────────────────────────────
// DevicePlayer — wraps a single WaveOutEvent + ISampleProvider pipeline
// ──────────────────────────────────────────────────────────────────────────────

internal class DevicePlayer : IDisposable
{
    private readonly AudioDevice _device;
    private WaveOutEvent? _waveOut;
    private InMemoryAudioProvider? _memProvider;
    private ISampleProvider? _pipeline;
    private bool _prepared;

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    public TimeSpan Position => _memProvider?.Position ?? TimeSpan.Zero;

    public event Action? PlaybackStopped;
    public event Action<string>? PlaybackError;

    public DevicePlayer(
        AudioDevice device,
        float[] samples,
        WaveFormat sourceFormat,
        DeviceChannel channel,
        int silencePaddingMs)
    {
        _device = device;
        _memProvider = new InMemoryAudioProvider(samples, sourceFormat);

        ISampleProvider pipeline = _memProvider;

        // Channel split for stereo-pair members
        if (channel == DeviceChannel.LeftOnly && sourceFormat.Channels >= 2)
            pipeline = new ChannelSelectProvider(pipeline, 0);
        else if (channel == DeviceChannel.RightOnly && sourceFormat.Channels >= 2)
            pipeline = new ChannelSelectProvider(pipeline, 1);

        // Volume
        var volumeProvider = new VolumeSampleProvider(pipeline) { Volume = device.Volume };
        pipeline = volumeProvider;

        // Latency padding (silence at start)
        if (silencePaddingMs > 0)
            pipeline = new SilencePaddedProvider(pipeline, silencePaddingMs);

        _pipeline = pipeline;
    }

    public void Prepare()
    {
        if (_prepared) return;
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = _device.WaveOutDeviceNumber,
            DesiredLatency = 300   // 300 ms is safer for Bluetooth
        };
        _waveOut.PlaybackStopped += (_, args) =>
        {
            if (args.Exception != null)
                PlaybackError?.Invoke($"[{_device.Name}] {args.Exception.Message}");
            else
                PlaybackStopped?.Invoke();
        };
        // Use 16-bit PCM — universally supported by all devices including Bluetooth
        _waveOut.Init(new SampleToWaveProvider16(_pipeline!));
        _prepared = true;
    }

    public void Start()
    {
        Prepare();
        _waveOut!.Play();
    }

    public void Pause()  => _waveOut?.Pause();
    public void Resume() => _waveOut?.Play();

    public void Stop()
    {
        _waveOut?.Stop();
        _memProvider?.Reset();
    }

    public void Seek(TimeSpan position)
    {
        _memProvider?.Seek(position);
    }

    public void Dispose()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        GC.SuppressFinalize(this);
    }
}

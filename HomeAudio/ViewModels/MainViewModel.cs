using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomeAudio.Models;
using HomeAudio.Services;
using Microsoft.Win32;

namespace HomeAudio.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AudioEngine         _engine      = new();
    private readonly SonosPlaybackManager _sonosMgr   = new();

    // Last decoded audio — shared between AudioEngine and SonosPlaybackManager
    private AudioDecoder.DecodedAudio? _decoded;

    // Position timer used when only Sonos devices are active (no WaveOut engine running)
    private System.Timers.Timer? _sonosPositionTimer;
    private DateTime             _sonosPlaybackStart;

    public MainViewModel()
    {
        _engine.PlaybackStopped += OnEnginePlaybackStopped;
        _engine.PlaybackError   += OnEnginePlaybackError;
        _engine.PositionChanged += OnEnginePositionChanged;

        RefreshDevicesCommand.Execute(null);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // WaveOut devices
    // ══════════════════════════════════════════════════════════════════════════

    public ObservableCollection<AudioDeviceViewModel> Devices { get; } = new();

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        foreach (var d in AudioDeviceEnumerator.GetOutputDevices())
            Devices.Add(new AudioDeviceViewModel(d));

        RebuildPairCandidates();
        UpdateStereoPairRoles();
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Sonos devices
    // ══════════════════════════════════════════════════════════════════════════

    public ObservableCollection<SonosDeviceViewModel> SonosDevices { get; } = new();

    [ObservableProperty]
    private bool _isSonosDiscovering;

    [ObservableProperty]
    private string _sonosStatus = "Not discovered yet. Click Discover to scan.";

    [RelayCommand]
    private async Task DiscoverSonosDevices()
    {
        IsSonosDiscovering = true;
        SonosStatus = "Scanning network for Sonos speakers…";

        try
        {
            var found = await _sonosMgr.DiscoverAsync(timeoutMs: 5000);

            // Keep existing active state for devices already in the list
            var existing = SonosDevices.ToDictionary(vm => vm.Model.Uuid, vm => vm);
            SonosDevices.Clear();

            foreach (var d in found)
            {
                if (existing.TryGetValue(d.Uuid, out var prev))
                {
                    d.IsActive         = prev.IsActive;
                    d.Volume           = prev.Volume;
                    d.LatencyOffsetMs  = prev.LatencyOffsetMs;
                    d.Channel          = prev.Channel;
                }
                SonosDevices.Add(new SonosDeviceViewModel(d)
                {
                    IsActive        = d.IsActive,
                    Volume          = d.Volume,
                    LatencyOffsetMs = d.LatencyOffsetMs
                });
            }

            SonosStatus = found.Count > 0
                ? $"Found {found.Count} Sonos device(s)."
                : "No Sonos speakers found. Ensure they are on the same network.";

            RebuildPairCandidates();
            UpdateStereoPairRoles();
        }
        catch (Exception ex)
        {
            SonosStatus = $"Discovery error: {ex.Message}";
        }
        finally
        {
            IsSonosDiscovering = false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Stereo pair — WaveOut
    // ══════════════════════════════════════════════════════════════════════════

    public ObservableCollection<AudioDeviceViewModel> AvailableDevicesForPair { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StereoPairValid))]
    private AudioDeviceViewModel? _stereoPairLeft;
    partial void OnStereoPairLeftChanged(AudioDeviceViewModel? value)
    {
        UpdateStereoPairRoles();
        if (value != null) value.IsActive = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StereoPairValid))]
    private AudioDeviceViewModel? _stereoPairRight;
    partial void OnStereoPairRightChanged(AudioDeviceViewModel? value)
    {
        UpdateStereoPairRoles();
        if (value != null) value.IsActive = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StereoPairValid))]
    [NotifyPropertyChangedFor(nameof(SonosStereoPairValid))]
    private bool _stereoPairEnabled;
    partial void OnStereoPairEnabledChanged(bool value) => UpdateStereoPairRoles();

    public bool StereoPairValid =>
        StereoPairEnabled &&
        StereoPairLeft  != null &&
        StereoPairRight != null &&
        StereoPairLeft  != StereoPairRight;

    // ══════════════════════════════════════════════════════════════════════════
    // Stereo pair — Sonos
    // ══════════════════════════════════════════════════════════════════════════

    public ObservableCollection<SonosDeviceViewModel> AvailableSonosForPair { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosStereoPairValid))]
    private SonosDeviceViewModel? _sonosStereoPairLeft;
    partial void OnSonosStereoPairLeftChanged(SonosDeviceViewModel? value)
    {
        UpdateStereoPairRoles();
        if (value != null) value.IsActive = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosStereoPairValid))]
    private SonosDeviceViewModel? _sonosStereoPairRight;
    partial void OnSonosStereoPairRightChanged(SonosDeviceViewModel? value)
    {
        UpdateStereoPairRoles();
        if (value != null) value.IsActive = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosStereoPairValid))]
    private bool _sonosStereoPairEnabled;
    partial void OnSonosStereoPairEnabledChanged(bool value) => UpdateStereoPairRoles();

    public bool SonosStereoPairValid =>
        SonosStereoPairEnabled      &&
        SonosStereoPairLeft  != null &&
        SonosStereoPairRight != null &&
        SonosStereoPairLeft  != SonosStereoPairRight;

    // ──────────────────────────────────────────────────────────────────────────

    private void RebuildPairCandidates()
    {
        AvailableDevicesForPair.Clear();
        foreach (var d in Devices) AvailableDevicesForPair.Add(d);

        AvailableSonosForPair.Clear();
        foreach (var d in SonosDevices) AvailableSonosForPair.Add(d);
    }

    private void UpdateStereoPairRoles()
    {
        foreach (var d in Devices)     { d.IsStereoPairMember = false; d.StereoPairRole = ""; }
        foreach (var d in SonosDevices){ d.IsStereoPairMember = false; d.StereoPairRole = ""; }

        if (StereoPairEnabled)
        {
            if (StereoPairLeft  != null) { StereoPairLeft.IsStereoPairMember  = true; StereoPairLeft.StereoPairRole  = "L"; }
            if (StereoPairRight != null) { StereoPairRight.IsStereoPairMember = true; StereoPairRight.StereoPairRole = "R"; }
        }

        if (SonosStereoPairEnabled)
        {
            if (SonosStereoPairLeft  != null) { SonosStereoPairLeft.IsStereoPairMember  = true; SonosStereoPairLeft.StereoPairRole  = "L"; }
            if (SonosStereoPairRight != null) { SonosStereoPairRight.IsStereoPairMember = true; SonosStereoPairRight.StereoPairRole = "R"; }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // File & playback
    // ══════════════════════════════════════════════════════════════════════════

    [ObservableProperty] private string _loadedFileName = "No file loaded";
    [ObservableProperty] private string _loadedFilePath = string.Empty;
    [ObservableProperty] private AppPlaybackState _playbackState = AppPlaybackState.Stopped;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionDisplay = "0:00";
    [ObservableProperty] private string _durationDisplay = "0:00";
    [ObservableProperty] private bool   _isSeeking;

    public bool IsPlaying => PlaybackState == AppPlaybackState.Playing;
    public bool IsStopped => PlaybackState == AppPlaybackState.Stopped;

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a|All files|*.*",
            Title  = "Select audio file"
        };
        if (dlg.ShowDialog() != true) return;

        LoadedFilePath = dlg.FileName;
        LoadedFileName = Path.GetFileName(dlg.FileName);
        _decoded       = null;   // invalidate cached decoded data

        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(dlg.FileName);
            DurationSeconds = reader.TotalTime.TotalSeconds;
            DurationDisplay = FormatTime(reader.TotalTime);
        }
        catch { /* handled at play time */ }
    }

    [RelayCommand]
    private async Task Play()
    {
        if (string.IsNullOrEmpty(LoadedFilePath)) { StatusMessage = "Open an audio file first."; return; }

        var waveOutDevices = Devices.Where(d => d.IsActive).Select(d => d.Model).ToList();
        var sonosDevices   = SonosDevices.Where(d => d.IsActive).Select(d => d.Model).ToList();

        if (waveOutDevices.Count == 0 && sonosDevices.Count == 0)
        {
            StatusMessage = "Select at least one output device.";
            return;
        }

        try
        {
            if (PlaybackState == AppPlaybackState.Paused)
            {
                _engine.Play();
                await _sonosMgr.PauseAsync();   // resume (Sonos Play after Pause resumes)
                await _sonosMgr.PlayAsync();
            }
            else
            {
                // Decode once — shared by WaveOut and Sonos
                if (_decoded == null)
                {
                    StatusMessage = "Decoding audio…";
                    _decoded = await Task.Run(() => AudioDecoder.Decode(LoadedFilePath));
                    DurationSeconds = _decoded.Duration.TotalSeconds;
                    DurationDisplay = FormatTime(_decoded.Duration);
                }

                // Build stereo pair objects
                StereoPair? waveOutPair = null;
                if (StereoPairValid)
                    waveOutPair = new StereoPair
                    {
                        IsEnabled   = true,
                        LeftDevice  = StereoPairLeft!.Model,
                        RightDevice = StereoPairRight!.Model
                    };

                SonosStereoPair? sonosPair = null;
                if (SonosStereoPairValid)
                    sonosPair = new SonosStereoPair
                    {
                        IsEnabled   = true,
                        LeftDevice  = SonosStereoPairLeft!.Model,
                        RightDevice = SonosStereoPairRight!.Model
                    };

                // Prepare Sonos (loads URIs, starts HTTP server) — do before WaveOut
                // so the stream is available when Sonos tries to buffer.
                if (sonosDevices.Count > 0)
                {
                    StatusMessage = "Preparing Sonos devices…";
                    await _sonosMgr.PrepareAsync(_decoded.Samples, _decoded.Format, sonosDevices, sonosPair);
                }

                // Load WaveOut engine
                if (waveOutDevices.Count > 0)
                    _engine.Load(_decoded, waveOutDevices, waveOutPair);

                // Start WaveOut (non-blocking)
                if (waveOutDevices.Count > 0)
                    _engine.Play();

                // Start Sonos (with per-device latency delay)
                if (sonosDevices.Count > 0)
                    _ = _sonosMgr.PlayAsync(referenceMs: 0);

                // When no WaveOut device is active the AudioEngine never starts its
                // position timer, so we run our own elapsed-time tracker for Sonos.
                if (sonosDevices.Count > 0 && waveOutDevices.Count == 0)
                    StartSonosPositionTimer();
            }

            PlaybackState = AppPlaybackState.Playing;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));

            int total = waveOutDevices.Count + sonosDevices.Count;
            StatusMessage = $"Playing on {waveOutDevices.Count} WaveOut + {sonosDevices.Count} Sonos device(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Playback error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Pause()
    {
        StopSonosPositionTimer();
        _engine.Pause();
        await _sonosMgr.PauseAsync();
        PlaybackState = AppPlaybackState.Paused;
        OnPropertyChanged(nameof(IsPlaying));
        StatusMessage = "Paused";
    }

    [RelayCommand]
    private async Task Stop()
    {
        StopSonosPositionTimer();
        _engine.Stop();
        await _sonosMgr.StopAsync();
        PlaybackState   = AppPlaybackState.Stopped;
        PositionSeconds = 0;
        PositionDisplay = "0:00";
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsStopped));
        StatusMessage = "Stopped";
    }

    public void BeginSeek() => IsSeeking = true;

    public void EndSeek()
    {
        IsSeeking = false;
        _engine.Seek(TimeSpan.FromSeconds(PositionSeconds));
        // Sonos seek via Stop + re-prepare is complex; skip for now
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Sonos-only position timer
    // (AudioEngine never starts when there are no WaveOut devices)
    // ══════════════════════════════════════════════════════════════════════════

    private void StartSonosPositionTimer()
    {
        StopSonosPositionTimer();
        _sonosPlaybackStart = DateTime.UtcNow;
        _sonosPositionTimer = new System.Timers.Timer(250);
        _sonosPositionTimer.Elapsed += OnSonosPositionTick;
        _sonosPositionTimer.AutoReset = true;
        _sonosPositionTimer.Start();
    }

    private void StopSonosPositionTimer()
    {
        _sonosPositionTimer?.Stop();
        _sonosPositionTimer?.Dispose();
        _sonosPositionTimer = null;
    }

    private void OnSonosPositionTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        var elapsed = DateTime.UtcNow - _sonosPlaybackStart;
        double pos = elapsed.TotalSeconds;

        if (pos >= DurationSeconds)
        {
            StopSonosPositionTimer();
            System.Windows.Application.Current?.Dispatcher.Invoke(async () =>
            {
                await _sonosMgr.StopAsync();
                PlaybackState   = AppPlaybackState.Stopped;
                PositionSeconds = 0;
                PositionDisplay = "0:00";
                OnPropertyChanged(nameof(IsPlaying));
                OnPropertyChanged(nameof(IsStopped));
                StatusMessage = "Playback finished.";
            });
            return;
        }

        if (IsSeeking) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PositionSeconds = pos;
            PositionDisplay = FormatTime(elapsed);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Bluetooth / Sound shortcuts
    // ══════════════════════════════════════════════════════════════════════════

    [RelayCommand] private void OpenBluetooth()    => BluetoothService.OpenBluetoothSettings();
    [RelayCommand] private void OpenSoundSettings()=> BluetoothService.OpenSoundSettings();

    // ══════════════════════════════════════════════════════════════════════════
    // Status bar
    // ══════════════════════════════════════════════════════════════════════════

    [ObservableProperty] private string _statusMessage =
        "Ready. Connect Bluetooth devices and refresh, or click Discover to find Sonos speakers.";

    // ══════════════════════════════════════════════════════════════════════════
    // Engine callbacks (arrive on a background thread)
    // ══════════════════════════════════════════════════════════════════════════

    private void OnEnginePlaybackError(string message) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PlaybackState = AppPlaybackState.Stopped;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));
            StatusMessage = $"Error: {message}";
        });

    private void OnEnginePlaybackStopped() =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PlaybackState   = AppPlaybackState.Stopped;
            PositionSeconds = 0;
            PositionDisplay = "0:00";
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));
            StatusMessage = "Playback finished.";
        });

    private void OnEnginePositionChanged(TimeSpan position)
    {
        if (IsSeeking) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PositionSeconds = position.TotalSeconds;
            PositionDisplay = FormatTime(position);
        });
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    public void Dispose()
    {
        StopSonosPositionTimer();
        _engine.Dispose();
        _sonosMgr.Dispose();
        GC.SuppressFinalize(this);
    }
}

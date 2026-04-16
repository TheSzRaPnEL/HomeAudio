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
    private readonly AudioEngine _engine = new();

    public MainViewModel()
    {
        _engine.PlaybackStopped += OnEnginePlaybackStopped;
        _engine.PlaybackError   += OnEnginePlaybackError;
        _engine.PositionChanged += OnEnginePositionChanged;

        RefreshDevicesCommand.Execute(null);
    }

    // ──────────────────────────────────────────────────────────────
    // Device list
    // ──────────────────────────────────────────────────────────────

    public ObservableCollection<AudioDeviceViewModel> Devices { get; } = new();

    [RelayCommand]
    private void RefreshDevices()
    {
        Devices.Clear();
        var devices = AudioDeviceEnumerator.GetOutputDevices();
        foreach (var d in devices)
            Devices.Add(new AudioDeviceViewModel(d));

        // Re-populate stereo pair combos
        AvailableDevicesForPair.Clear();
        foreach (var d in Devices)
            AvailableDevicesForPair.Add(d);

        UpdateStereoPairRoles();
    }

    // ──────────────────────────────────────────────────────────────
    // Stereo pair
    // ──────────────────────────────────────────────────────────────

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
    private bool _stereoPairEnabled;

    public bool StereoPairValid =>
        StereoPairEnabled
        && StereoPairLeft != null
        && StereoPairRight != null
        && StereoPairLeft != StereoPairRight;

    private void UpdateStereoPairRoles()
    {
        foreach (var d in Devices)
        {
            d.IsStereoPairMember = false;
            d.StereoPairRole = string.Empty;
        }

        if (!StereoPairEnabled) return;

        if (StereoPairLeft != null)
        {
            StereoPairLeft.IsStereoPairMember = true;
            StereoPairLeft.StereoPairRole = "L";
        }
        if (StereoPairRight != null)
        {
            StereoPairRight.IsStereoPairMember = true;
            StereoPairRight.StereoPairRole = "R";
        }
    }

    // ──────────────────────────────────────────────────────────────
    // File & playback
    // ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _loadedFileName = "No file loaded";

    [ObservableProperty]
    private string _loadedFilePath = string.Empty;

    [ObservableProperty]
    private AppPlaybackState _playbackState = AppPlaybackState.Stopped;

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds = 1;

    [ObservableProperty]
    private string _positionDisplay = "0:00";

    [ObservableProperty]
    private string _durationDisplay = "0:00";

    [ObservableProperty]
    private bool _isSeeking; // true while the user drags the slider

    public bool IsPlaying => PlaybackState == AppPlaybackState.Playing;
    public bool IsStopped => PlaybackState == AppPlaybackState.Stopped;

    [RelayCommand]
    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Audio files|*.mp3;*.wav;*.flac;*.aac;*.ogg;*.m4a|All files|*.*",
            Title = "Select audio file"
        };
        if (dlg.ShowDialog() != true) return;

        LoadedFilePath = dlg.FileName;
        LoadedFileName = Path.GetFileName(dlg.FileName);

        // Read duration without full decode
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(dlg.FileName);
            DurationSeconds = reader.TotalTime.TotalSeconds;
            DurationDisplay = FormatTime(reader.TotalTime);
        }
        catch { /* handled at play time */ }
    }

    [RelayCommand]
    private void Play()
    {
        var activeDevices = Devices.Where(d => d.IsActive).Select(d => d.Model).ToList();

        if (activeDevices.Count == 0)
        {
            StatusMessage = "Select at least one output device.";
            return;
        }

        if (string.IsNullOrEmpty(LoadedFilePath))
        {
            StatusMessage = "Open an audio file first.";
            return;
        }

        try
        {
            if (PlaybackState == AppPlaybackState.Paused)
            {
                _engine.Play();
            }
            else
            {
                StereoPair? pair = null;
                if (StereoPairValid)
                {
                    pair = new StereoPair
                    {
                        IsEnabled = true,
                        LeftDevice = StereoPairLeft!.Model,
                        RightDevice = StereoPairRight!.Model
                    };
                }

                _engine.Load(LoadedFilePath, activeDevices, pair);
                _engine.Play();
            }

            PlaybackState = AppPlaybackState.Playing;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));
            StatusMessage = $"Playing on {activeDevices.Count} device(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Playback error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Pause()
    {
        _engine.Pause();
        PlaybackState = AppPlaybackState.Paused;
        OnPropertyChanged(nameof(IsPlaying));
        StatusMessage = "Paused";
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        PlaybackState = AppPlaybackState.Stopped;
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
    }

    // ──────────────────────────────────────────────────────────────
    // Bluetooth shortcuts
    // ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void OpenBluetooth() => BluetoothService.OpenBluetoothSettings();

    [RelayCommand]
    private void OpenSoundSettings() => BluetoothService.OpenSoundSettings();

    // ──────────────────────────────────────────────────────────────
    // Status bar
    // ──────────────────────────────────────────────────────────────

    [ObservableProperty]
    private string _statusMessage = "Ready. Connect Bluetooth devices, then refresh the device list.";

    // ──────────────────────────────────────────────────────────────
    // Engine callbacks (come on a background thread)
    // ──────────────────────────────────────────────────────────────

    private void OnEnginePlaybackError(string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PlaybackState = AppPlaybackState.Stopped;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));
            StatusMessage = $"Error: {message}";
        });
    }

    private void OnEnginePlaybackStopped()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PlaybackState = AppPlaybackState.Stopped;
            PositionSeconds = 0;
            PositionDisplay = "0:00";
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsStopped));
            StatusMessage = "Playback finished.";
        });
    }

    private void OnEnginePositionChanged(TimeSpan position)
    {
        if (IsSeeking) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            PositionSeconds = position.TotalSeconds;
            PositionDisplay = FormatTime(position);
        });
    }

    private static string FormatTime(TimeSpan t)
        => t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    public void Dispose()
    {
        _engine.Dispose();
        GC.SuppressFinalize(this);
    }
}

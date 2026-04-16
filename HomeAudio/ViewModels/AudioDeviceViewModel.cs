using CommunityToolkit.Mvvm.ComponentModel;
using HomeAudio.Models;

namespace HomeAudio.ViewModels;

public partial class AudioDeviceViewModel : ObservableObject
{
    private readonly AudioDevice _model;

    public AudioDeviceViewModel(AudioDevice model)
    {
        _model = model;
    }

    public AudioDevice Model => _model;

    public string Name => _model.Name;
    public bool IsBluetoothDevice => _model.IsBluetoothDevice;
    public int WaveOutDeviceNumber => _model.WaveOutDeviceNumber;

    [ObservableProperty]
    private bool _isActive;

    partial void OnIsActiveChanged(bool value)
    {
        _model.IsActive = value;
    }

    [ObservableProperty]
    private float _volume = 1.0f;

    partial void OnVolumeChanged(float value)
    {
        _model.Volume = value;
    }

    [ObservableProperty]
    private int _latencyOffsetMs = 0;

    partial void OnLatencyOffsetMsChanged(int value)
    {
        _model.LatencyOffsetMs = value;
    }

    [ObservableProperty]
    private DeviceChannel _channel = DeviceChannel.Stereo;

    partial void OnChannelChanged(DeviceChannel value)
    {
        _model.Channel = value;
    }

    // Indicates this device is part of the active stereo pair
    [ObservableProperty]
    private bool _isStereoPairMember;

    [ObservableProperty]
    private string _stereoPairRole = string.Empty; // "Left" | "Right" | ""

    public string DeviceIcon => IsBluetoothDevice ? "🔵" : "🔊";
}

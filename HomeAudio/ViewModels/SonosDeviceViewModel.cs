using CommunityToolkit.Mvvm.ComponentModel;
using HomeAudio.Models;

namespace HomeAudio.ViewModels;

public partial class SonosDeviceViewModel : ObservableObject
{
    public SonosDevice Model { get; }

    public SonosDeviceViewModel(SonosDevice model) => Model = model;

    public string Name      => Model.Name;
    public string ModelName => Model.ModelName;
    public string IpAddress => Model.IpAddress;
    public string DisplayName => Model.DisplayName;

    [ObservableProperty]
    private bool _isActive;
    partial void OnIsActiveChanged(bool value) => Model.IsActive = value;

    [ObservableProperty]
    private float _volume = 0.5f;
    partial void OnVolumeChanged(float value) => Model.Volume = value;

    [ObservableProperty]
    private int _latencyOffsetMs = 2000;
    partial void OnLatencyOffsetMsChanged(int value) => Model.LatencyOffsetMs = value;

    [ObservableProperty]
    private DeviceChannel _channel = DeviceChannel.Stereo;
    partial void OnChannelChanged(DeviceChannel value) => Model.Channel = value;

    [ObservableProperty]
    private bool _isStereoPairMember;

    [ObservableProperty]
    private string _stereoPairRole = string.Empty; // "L" | "R" | ""

    public string DeviceIcon => "📡";  // Sonos = network speaker
}

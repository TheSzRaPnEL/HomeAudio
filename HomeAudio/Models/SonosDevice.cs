namespace HomeAudio.Models;

public class SonosDevice
{
    public string Uuid      { get; set; } = string.Empty;
    public string Name      { get; set; } = string.Empty;   // Zone / room name
    public string ModelName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public int    Port      { get; set; } = 1400;

    public bool IsActive { get; set; }
    public float Volume  { get; set; } = 0.5f;  // 0–1, maps to Sonos 0–100

    /// <summary>
    /// Positive value (ms) = delay Sonos Play() call relative to WaveOut start.
    /// Compensates for Sonos buffering latency (~1–3 s depending on device).
    /// </summary>
    public int LatencyOffsetMs { get; set; } = 2000;

    public DeviceChannel Channel { get; set; } = DeviceChannel.Stereo;

    /// <summary>Set at playback time by SonosPlaybackManager to the HTTP stream URL.</summary>
    public string StreamUrl { get; set; } = string.Empty;

    public string DisplayName =>
        string.IsNullOrEmpty(Name) ? IpAddress : $"{Name}  ({ModelName})";
}

/// <summary>Stereo pair backed by two Sonos speakers.</summary>
public class SonosStereoPair
{
    public SonosDevice? LeftDevice  { get; set; }
    public SonosDevice? RightDevice { get; set; }
    public bool IsEnabled { get; set; }

    public bool IsValid =>
        LeftDevice  != null &&
        RightDevice != null &&
        LeftDevice  != RightDevice;
}

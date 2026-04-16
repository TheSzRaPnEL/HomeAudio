namespace HomeAudio.Models;

public enum DeviceChannel
{
    Stereo,
    LeftOnly,
    RightOnly
}

public class AudioDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // WaveOut device index (-1 = not assigned)
    public int WaveOutDeviceNumber { get; set; } = -1;

    // WASAPI device ID for BT detection
    public string MmDeviceId { get; set; } = string.Empty;

    public bool IsBluetoothDevice { get; set; }
    public bool IsActive { get; set; }

    // Playback settings
    public float Volume { get; set; } = 1.0f;
    public int LatencyOffsetMs { get; set; } = 0;
    public DeviceChannel Channel { get; set; } = DeviceChannel.Stereo;

    public override string ToString() => Name;
}

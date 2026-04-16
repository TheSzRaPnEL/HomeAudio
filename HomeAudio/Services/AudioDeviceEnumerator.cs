using HomeAudio.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HomeAudio.Services;

public static class AudioDeviceEnumerator
{
    /// <summary>
    /// Returns all active audio output devices, annotating Bluetooth devices.
    /// </summary>
    public static List<AudioDevice> GetOutputDevices()
    {
        var result = new List<AudioDevice>();

        // Build a map of WaveOut name → WASAPI device for BT detection
        var mmDeviceMap = BuildMmDeviceMap();

        // WaveOut device count (index 0..N-1); index -1 is the default mapper
        int deviceCount = WaveOut.DeviceCount;
        for (int i = 0; i < deviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            bool isBluetooth = false;
            string mmDeviceId = string.Empty;

            if (mmDeviceMap.TryGetValue(caps.ProductName, out var mmDevice))
            {
                isBluetooth = IsBluetoothDevice(mmDevice);
                mmDeviceId = mmDevice.ID;
            }

            result.Add(new AudioDevice
            {
                Id = $"waveout_{i}",
                Name = caps.ProductName,
                WaveOutDeviceNumber = i,
                MmDeviceId = mmDeviceId,
                IsBluetoothDevice = isBluetooth,
                IsActive = false,
                Volume = 1.0f,
                LatencyOffsetMs = 0,
                Channel = DeviceChannel.Stereo
            });
        }

        return result;
    }

    private static Dictionary<string, MMDevice> BuildMmDeviceMap()
    {
        var map = new Dictionary<string, MMDevice>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(
                DataFlow.Render, DeviceState.Active);
            foreach (var d in devices)
            {
                // WASAPI friendly name often matches WaveOut product name (first 31 chars)
                string name = d.FriendlyName;
                if (name.Length > 31) name = name[..31];
                map.TryAdd(name, d);

                // Also add full friendly name
                map.TryAdd(d.FriendlyName, d);
            }
        }
        catch
        {
            // WASAPI may not be available; BT detection just won't work
        }
        return map;
    }

    private static bool IsBluetoothDevice(MMDevice device)
    {
        try
        {
            // Bluetooth devices have a specific interface GUID in their device ID
            // Common pattern: includes "BTHENUM" or "BTHLEDevice"
            string id = device.ID;
            return id.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
                || id.Contains("BTHLEDevice", StringComparison.OrdinalIgnoreCase)
                || id.Contains("BluetoothDevice", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

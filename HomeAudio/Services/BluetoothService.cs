using System.Diagnostics;

namespace HomeAudio.Services;

/// <summary>
/// Helpers for Bluetooth device management on Windows.
/// Opens the system Bluetooth settings page so the user can pair / connect devices.
/// Actual BT audio connection is managed by Windows; once paired and connected,
/// the device appears in the WaveOut enumeration automatically.
/// </summary>
public static class BluetoothService
{
    /// <summary>Opens Windows Bluetooth settings (Devices page).</summary>
    public static void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open BT settings: {ex.Message}");
        }
    }

    /// <summary>Opens Windows Sound output device settings.</summary>
    public static void OpenSoundSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:sound",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open Sound settings: {ex.Message}");
        }
    }
}

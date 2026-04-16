# HomeAudio — Multi-Device Bluetooth Audio Player

A Windows desktop application for playing audio simultaneously on multiple output devices (Bluetooth and wired), with stereo-pair mode for two Bluetooth speakers.

## Features

- **Multi-device playback** — play the same audio on any combination of output devices at once
- **Stereo pair** — assign one device as Left channel and another as Right, creating a virtual stereo speaker setup from two Bluetooth speakers
- **Per-device controls**:
  - Independent volume per device
  - Latency offset (±500 ms) for manual sync compensation between devices with different Bluetooth latency
- **Transport controls** — Play, Pause, Stop, seek bar with position display
- **Bluetooth shortcuts** — open Windows Bluetooth & Sound settings directly from the app
- **Supported formats** — MP3, WAV, FLAC, AAC, OGG, M4A (via NAudio)

## Architecture

```
HomeAudio/
├── Models/
│   ├── AudioDevice.cs        — device data model
│   ├── StereoPair.cs         — stereo pair config
│   └── PlaybackState.cs      — playback state enum + info
├── Services/
│   ├── AudioEngine.cs        — core multi-device playback engine
│   ├── AudioDeviceEnumerator.cs  — lists WaveOut devices + BT detection
│   ├── InMemoryAudioProvider.cs  — pre-decoded shared audio buffer
│   ├── ChannelSelectProvider.cs  — mono channel extractor for stereo pair
│   ├── SilencePaddedProvider.cs  — latency compensation silence prefix
│   └── BluetoothService.cs   — opens Windows BT/Sound settings
├── ViewModels/
│   ├── MainViewModel.cs      — main MVVM view model
│   └── AudioDeviceViewModel.cs   — per-device binding
├── Converters/
│   └── Converters.cs         — WPF value converters
├── Resources/
│   └── Styles.xaml           — dark theme styles
├── MainWindow.xaml / .xaml.cs
└── App.xaml / .xaml.cs
```

## Requirements

- Windows 10 (1809) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Bluetooth devices already paired in Windows before launching the app

## Build & Run

```bash
# Restore packages and build
dotnet build HomeAudio.sln

# Run
dotnet run --project HomeAudio/HomeAudio.csproj
```

Or open `HomeAudio.sln` in Visual Studio 2022 and press F5.

## Usage

1. **Pair your Bluetooth speakers** in Windows Settings → Bluetooth (or click the 🔵 Bluetooth button in the app)
2. Click **↻ Refresh** in the device list to see all audio output devices
3. **Check** the devices you want to use
4. Optionally configure a **Stereo Pair** — select a Left and Right device
5. Click **📂 Open File** to load an MP3 or other audio file
6. Press **▶ Play**
7. Adjust **Volume** and **Delay** sliders per device if sync is off

## Sync Notes

Bluetooth audio has inherent latency that varies by device (typically 100–300 ms). To sync two Bluetooth speakers:
- Increase the **Delay** (ms) on the faster device until both speakers sound in sync
- The stereo pair will still work even if not perfectly synced — but for music, tuning is recommended

## Dependencies

- [NAudio](https://github.com/naudio/NAudio) — audio decoding and WaveOut playback
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) — MVVM source generators

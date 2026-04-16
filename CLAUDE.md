# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build
dotnet build HomeAudio.sln

# Run
dotnet run --project HomeAudio/HomeAudio.csproj
```

No test suite exists — testing is manual on Windows.

## Architecture Overview

HomeAudio is a **WPF/.NET 8** desktop app (Windows-only) that plays audio simultaneously across multiple output devices (Bluetooth speakers, wired audio, Sonos). It solves the Windows limitation of single-device audio output.

**Stack:** NAudio 2.2.1 (audio engine), CommunityToolkit.Mvvm 8.3.2 (MVVM source generators), WPF + XAML.

### Audio Pipeline (WaveOut devices)

Each active WaveOut device gets its own `DevicePlayer` (in `AudioEngine.cs`) with a composable `ISampleProvider` chain:

```
InMemoryAudioProvider (shared float[] buffer, independent read pos)
  → ChannelSelectProvider (if stereo pair: extract L or R mono)
  → VolumeSampleProvider (NAudio built-in, per-device volume)
  → SilencePaddedProvider (prepend silence for latency compensation)
  → SampleToWaveProvider16
  → WaveOutEvent → speaker
```

The audio file is pre-decoded once by `AudioDecoder` into a `float[]` buffer shared by all devices. `InMemoryAudioProvider` wraps this buffer with an independent read position per device, so each plays back without re-reading the file.

### Latency Compensation

Each device has a `LatencyOffsetMs` (user-adjustable ±500ms). `AudioEngine` normalizes offsets so the minimum is 0, then implements delay as silence prepended by `SilencePaddedProvider`. Stereo pair members use `ChannelSelectProvider` for L/R split instead of silence padding.

### Sonos Integration

Sonos uses a different path than WaveOut:

1. **`SonosDiscovery`** — SSDP multicast (`urn:schemas-upnp-org:device:ZonePlayer:1`) to find devices, then fetches XML device descriptions.
2. **`AudioStreamServer`** — Local HTTP server serving the decoded buffer as WAV streams (Stereo, LeftOnly, RightOnly variants) to Sonos speakers.
3. **`SonosController`** — UPnP AVTransport SOAP commands (`SetAVTransportURI`, `Play`, `Pause`, `Stop`).
4. **`SonosPlaybackManager`** — Orchestrates: prepare HTTP streams → push URIs to Sonos via UPnP → play with latency delay offsets.

### MVVM Structure

- **`MainViewModel`** — Central hub: device collections, file loading, playback state (`AppPlaybackState` enum), position tracking (250ms timer), stereo pair selection/validation.
- **`AudioDeviceViewModel` / `SonosDeviceViewModel`** — Thin wrappers around model objects for WPF binding (checkboxes, volume sliders, latency sliders).
- CommunityToolkit.Mvvm source generators: `[ObservableProperty]`, `[RelayCommand]`.

### Stereo Pair Modes

Both WaveOut and Sonos support stereo pairs. One device plays the left channel, the other plays the right. `ChannelSelectProvider` extracts the appropriate mono channel from the stereo float[] buffer.

### Device Enumeration

`AudioDeviceEnumerator` lists WaveOut devices via NAudio, then cross-references with WASAPI `MMDevice` to detect Bluetooth. BT devices are identified by device IDs containing `BTHENUM`, `BTHLEDevice`, or `BluetoothDevice`.

## Key Design Notes

- The app does not pair Bluetooth devices — they must already be paired in Windows Settings before the app can enumerate them.
- Sonos seek is not implemented (would require restarting the HTTP stream mid-decode).
- Position tracking: for WaveOut playback uses `AudioEngine` position; for Sonos-only playback uses elapsed-time calculation since Sonos has no reliable position query.
- Supported formats: MP3, WAV, FLAC, AAC, OGG, M4A (via `NAudio.AudioFileReader`).
- Target: Windows 10 (1809 / build 17763) or Windows 11.

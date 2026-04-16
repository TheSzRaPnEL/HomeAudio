using HomeAudio.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Captures audio from a WASAPI input device and writes samples into a thread-safe
/// ring buffer. Multiple <see cref="MicSampleProvider"/> instances can read from the
/// buffer independently, enabling simultaneous live output to multiple WaveOut devices.
/// </summary>
public class MicrophoneCapture : IDisposable
{
    // 6-second ring at 48000 Hz stereo = 48000 * 2 * 6 = 576000 floats (~2.2 MB)
    private const int RingCapacity = 576000;

    private readonly float[] _ring = new float[RingCapacity];
    private long   _writtenTotal = 0;   // total float samples written (absolute)
    private readonly object _writeLock  = new();

    private WasapiCapture? _capture;
    private WaveFormat     _captureFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    /// <summary>
    /// IEEE float WaveFormat matching the capture device's sample rate and channel count.
    /// Consumers should query this after <see cref="Start"/> has been called.
    /// </summary>
    public WaveFormat WaveFormat =>
        WaveFormat.CreateIeeeFloatWaveFormat(_captureFormat.SampleRate, _captureFormat.Channels);

    /// <summary>
    /// Total float samples written so far. Used by <see cref="MicSampleProvider"/> to anchor
    /// its initial read position.
    /// </summary>
    public long WrittenTotal
    {
        get { lock (_writeLock) return _writtenTotal; }
    }

    // ──────────────────────────────────────────────────────────────
    // Device enumeration
    // ──────────────────────────────────────────────────────────────

    public static List<InputDevice> GetInputDevices()
    {
        var result = new List<InputDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            foreach (var d in devices)
            {
                result.Add(new InputDevice
                {
                    Id        = d.ID,
                    Name      = d.FriendlyName,
                    IsDefault = d.ID == defaultDevice.ID
                });
            }
        }
        catch { /* WASAPI unavailable */ }
        return result;
    }

    // ──────────────────────────────────────────────────────────────
    // Capture lifecycle
    // ──────────────────────────────────────────────────────────────

    public void Start(InputDevice device)
    {
        Stop();

        MMDevice? mmDevice = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            mmDevice = enumerator.GetDevice(device.Id);
        }
        catch { }

        _capture = mmDevice != null
            ? new WasapiCapture(mmDevice)
            : new WasapiCapture();

        _captureFormat = _capture.WaveFormat;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        if (_capture == null) return;
        _capture.StopRecording();
        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _capture = null;

        lock (_writeLock) { _writtenTotal = 0; }
    }

    // ──────────────────────────────────────────────────────────────
    // Reader creation
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="MicSampleProvider"/> anchored at the current write
    /// position.  Call this just before starting a WaveOut device player so that the
    /// reader sees fresh data rather than stale ring content.
    /// </summary>
    public MicSampleProvider CreateReader() => new(this);

    // ──────────────────────────────────────────────────────────────
    // Internal ring-buffer read used by MicSampleProvider
    // ──────────────────────────────────────────────────────────────

    internal int ReadSamples(float[] dest, int destOffset, int count, ref long readerTotal)
    {
        long written;
        lock (_writeLock) { written = _writtenTotal; }

        long available = written - readerTotal;

        // Reader has fallen so far behind it would read overwritten data — skip forward.
        if (available > RingCapacity - 4096)
        {
            readerTotal = written - RingCapacity / 2;
            available   = written - readerTotal;
        }

        if (available <= 0)
        {
            // Underrun — output silence and wait for more mic data.
            Array.Clear(dest, destOffset, count);
            return count;
        }

        int toRead  = (int)Math.Min(count, available);
        int readPos = (int)(readerTotal % RingCapacity);
        int toEnd   = RingCapacity - readPos;

        if (toRead <= toEnd)
        {
            Array.Copy(_ring, readPos, dest, destOffset, toRead);
        }
        else
        {
            Array.Copy(_ring, readPos, dest, destOffset, toEnd);
            Array.Copy(_ring, 0,       dest, destOffset + toEnd, toRead - toEnd);
        }

        readerTotal += toRead;

        if (toRead < count)
            Array.Clear(dest, destOffset + toRead, count - toRead);

        return count;
    }

    // ──────────────────────────────────────────────────────────────
    // WASAPI callback
    // ──────────────────────────────────────────────────────────────

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        float[] samples = ConvertToFloat(e.Buffer, e.BytesRecorded, _captureFormat);

        lock (_writeLock)
        {
            int writePos = (int)(_writtenTotal % RingCapacity);
            int count    = samples.Length;
            int toEnd    = RingCapacity - writePos;

            if (count <= toEnd)
            {
                Array.Copy(samples, 0, _ring, writePos, count);
            }
            else
            {
                Array.Copy(samples, 0,     _ring, writePos, toEnd);
                Array.Copy(samples, toEnd, _ring, 0, count - toEnd);
            }

            _writtenTotal += count;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Format conversion helpers
    // ──────────────────────────────────────────────────────────────

    private static float[] ConvertToFloat(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            int n = bytesRecorded / 4;
            var f = new float[n];
            Buffer.BlockCopy(buffer, 0, f, 0, bytesRecorded);
            return f;
        }

        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            int n = bytesRecorded / 2;
            var f = new float[n];
            for (int i = 0; i < n; i++)
                f[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
            return f;
        }

        if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 32)
        {
            int n = bytesRecorded / 4;
            var f = new float[n];
            for (int i = 0; i < n; i++)
                f[i] = BitConverter.ToInt32(buffer, i * 4) / (float)int.MaxValue;
            return f;
        }

        // Fallback: assume raw 32-bit float
        {
            int n = bytesRecorded / 4;
            var f = new float[n];
            Buffer.BlockCopy(buffer, 0, f, 0, bytesRecorded);
            return f;
        }
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}

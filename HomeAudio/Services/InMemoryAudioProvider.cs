using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// An ISampleProvider backed by a pre-decoded float array.
/// Multiple DevicePlayer instances share the same sample data but maintain
/// independent read positions, enabling synchronised multi-device playback.
/// </summary>
public class InMemoryAudioProvider : ISampleProvider
{
    private readonly float[] _samples;
    private int _position; // sample index (all channels interleaved)

    public InMemoryAudioProvider(float[] samples, WaveFormat format)
    {
        _samples = samples;
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public TimeSpan Position
    {
        get
        {
            long frameIndex = _position / WaveFormat.Channels;
            return TimeSpan.FromSeconds((double)frameIndex / WaveFormat.SampleRate);
        }
    }

    public TimeSpan Duration
    {
        get
        {
            long totalFrames = _samples.Length / WaveFormat.Channels;
            return TimeSpan.FromSeconds((double)totalFrames / WaveFormat.SampleRate);
        }
    }

    public void Seek(TimeSpan position)
    {
        long frameIndex = (long)(position.TotalSeconds * WaveFormat.SampleRate);
        _position = (int)(frameIndex * WaveFormat.Channels);
        _position = Math.Clamp(_position, 0, _samples.Length);
    }

    public void Reset() => _position = 0;

    public int Read(float[] buffer, int offset, int count)
    {
        int available = _samples.Length - _position;
        int toRead = Math.Min(count, available);
        if (toRead <= 0) return 0;

        Array.Copy(_samples, _position, buffer, offset, toRead);
        _position += toRead;
        return toRead;
    }
}

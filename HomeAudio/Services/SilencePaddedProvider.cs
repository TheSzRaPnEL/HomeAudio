using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Prepends a silence buffer to a sample provider.
/// Used for latency compensation: positive offsetMs delays this device's audio,
/// negative offsetMs is handled by delaying the other device instead.
/// </summary>
public class SilencePaddedProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private int _silenceSamplesRemaining;

    public SilencePaddedProvider(ISampleProvider source, int silenceMs)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
        _silenceSamplesRemaining = (int)(silenceMs / 1000.0 * WaveFormat.SampleRate) * WaveFormat.Channels;
        if (_silenceSamplesRemaining < 0) _silenceSamplesRemaining = 0;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_silenceSamplesRemaining > 0)
        {
            int silenceToWrite = Math.Min(_silenceSamplesRemaining, count);
            Array.Clear(buffer, offset, silenceToWrite);
            _silenceSamplesRemaining -= silenceToWrite;

            if (silenceToWrite == count) return count;

            // Fill rest from source
            int remainder = count - silenceToWrite;
            int sourceRead = _source.Read(buffer, offset + silenceToWrite, remainder);
            return silenceToWrite + sourceRead;
        }

        return _source.Read(buffer, offset, count);
    }
}

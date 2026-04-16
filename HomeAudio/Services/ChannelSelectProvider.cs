using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Extracts a single channel from a stereo (or multi-channel) source,
/// producing a mono output. Use channelIndex=0 for left, 1 for right.
/// </summary>
public class ChannelSelectProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channelIndex;
    private float[]? _sourceBuffer;

    public ChannelSelectProvider(ISampleProvider source, int channelIndex)
    {
        if (source.WaveFormat.Channels < 2)
            throw new ArgumentException("Source must have at least 2 channels.");

        _source = source;
        _channelIndex = channelIndex;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int channels = _source.WaveFormat.Channels;
        int sourceSamplesNeeded = count * channels;

        if (_sourceBuffer == null || _sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int samplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int outSamples = samplesRead / channels;

        for (int i = 0; i < outSamples; i++)
            buffer[offset + i] = _sourceBuffer[i * channels + _channelIndex];

        return outSamples;
    }
}

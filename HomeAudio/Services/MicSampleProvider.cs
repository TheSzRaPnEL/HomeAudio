using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// An <see cref="ISampleProvider"/> that streams live microphone audio from a
/// <see cref="MicrophoneCapture"/> ring buffer.  Each instance keeps its own
/// independent read position so multiple output devices can be driven simultaneously
/// from the same capture source.
/// </summary>
public class MicSampleProvider : ISampleProvider
{
    private readonly MicrophoneCapture _capture;
    private long _readTotal;

    public WaveFormat WaveFormat => _capture.WaveFormat;

    /// <summary>
    /// Instantiated only via <see cref="MicrophoneCapture.CreateReader"/>.
    /// Anchors the read position at the current write head so the reader hears
    /// audio from the moment the DevicePlayer is started.
    /// </summary>
    internal MicSampleProvider(MicrophoneCapture capture)
    {
        _capture   = capture;
        _readTotal = capture.WrittenTotal;
    }

    public int Read(float[] buffer, int offset, int count)
        => _capture.ReadSamples(buffer, offset, count, ref _readTotal);
}

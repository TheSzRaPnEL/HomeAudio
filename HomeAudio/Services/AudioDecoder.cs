using NAudio.Wave;

namespace HomeAudio.Services;

/// <summary>
/// Decodes an audio file to a flat float[] of interleaved PCM samples.
/// Shared by AudioEngine (WaveOut) and SonosPlaybackManager (HTTP streaming)
/// so the file is read only once per playback session.
/// </summary>
public static class AudioDecoder
{
    public record DecodedAudio(float[] Samples, WaveFormat Format, TimeSpan Duration);

    public static DecodedAudio Decode(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        var format   = reader.WaveFormat;
        var duration = reader.TotalTime;

        var buffer    = new float[format.SampleRate * format.Channels]; // 1-second chunks
        var sampleList = new List<float>(capacity: (int)(duration.TotalSeconds * format.SampleRate * format.Channels) + 1024);

        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                sampleList.Add(buffer[i]);
        }

        return new DecodedAudio(sampleList.ToArray(), format, duration);
    }
}

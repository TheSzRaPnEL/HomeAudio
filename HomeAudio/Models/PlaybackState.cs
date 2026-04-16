using System.IO;

namespace HomeAudio.Models;

public enum AppPlaybackState
{
    Stopped,
    Playing,
    Paused
}

public class PlaybackInfo
{
    public AppPlaybackState State { get; set; } = AppPlaybackState.Stopped;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public string? FilePath { get; set; }
    public string FileName => FilePath != null ? Path.GetFileName(FilePath) : string.Empty;
}

namespace HomeAudio.Models;

public class StereoPair
{
    public string Name { get; set; } = "Stereo Pair";
    public AudioDevice? LeftDevice { get; set; }
    public AudioDevice? RightDevice { get; set; }
    public bool IsEnabled { get; set; }

    public bool IsValid => LeftDevice != null && RightDevice != null
                        && LeftDevice != RightDevice;
}

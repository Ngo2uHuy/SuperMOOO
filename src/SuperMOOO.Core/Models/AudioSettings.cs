namespace SuperMOOO.Core.Models;

// Section "audio.bgm": danh sách nhạc nền dùng chung (tối đa 5 bài, chọn ngẫu nhiên mỗi video).
public sealed class AudioBgmSettings
{
    public List<string> Files { get; set; } = [];
    public double Volume { get; set; } = 0.12;
    public bool Enabled { get; set; }
}

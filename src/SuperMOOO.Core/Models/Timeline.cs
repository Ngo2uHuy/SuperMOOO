namespace SuperMOOO.Core.Models;

public enum TimelineTrack { Video, Voiceover, Music, Subtitle }

public sealed record TimelineClip(Guid Id, TimelineTrack Track, string Path, double Start, double In, double Out,
    double SourceDuration, string Text = "", double Volume = 1, bool Muted = false)
{
    public double Duration => Out - In;
    public double End => Start + Duration;
    public static TimelineClip Media(TimelineTrack track, string path, double duration, double start = 0) =>
        new(Guid.NewGuid(), track, path, start, 0, duration, duration);
}

/// <summary>Video nối liên tiếp (ripple); audio và phụ đề có vị trí độc lập trên thời gian đầu ra.</summary>
public sealed record TimelineProject(int Version, string Name, IReadOnlyList<TimelineClip> Clips,
    string Aspect = "16:9", int Height = 720, int Fps = 30)
{
    public static TimelineProject Empty => new(1, "Dự án mới", []);
    public double Duration => Clips.Where(c => c.Track == TimelineTrack.Video).Sum(c => c.Duration);
}

public static class TimelineEditing
{
    public const double MinDuration = 0.08;

    public static TimelineProject Normalize(TimelineProject project)
    {
        double position = 0;
        return project with { Clips = project.Clips.Select(c =>
        {
            if (c.Track != TimelineTrack.Video) return c;
            var clip = c with { Start = position };
            position += clip.Duration;
            return clip;
        }).ToArray() };
    }

    public static void Validate(TimelineProject project, bool forRender = false)
    {
        if (project.Version != 1) throw new ArgumentException("Phiên bản dự án timeline chưa được hỗ trợ.");
        if (project.Name is null) throw new ArgumentException("Tên dự án không hợp lệ.");
        if (project.Clips is null || project.Clips.Count > 500) throw new ArgumentException("Timeline tối đa 500 thành phần.");
        if (project.Clips.Any(c => c is null)) throw new ArgumentException("Dữ liệu clip không hợp lệ.");
        if (project.Clips.Select(c => c.Id).Distinct().Count() != project.Clips.Count) throw new ArgumentException("Clip bị trùng mã.");
        if (project.Aspect is not ("16:9" or "9:16" or "1:1" or "Original") || project.Height is < 144 or > 2160 || project.Fps is < 1 or > 60)
            throw new ArgumentException("Thông số khung hình không hợp lệ.");
        double videoStart = 0;
        foreach (var c in project.Clips)
        {
            if (!Enum.IsDefined(c.Track) || c.Id == Guid.Empty || c.Text is null ||
                !double.IsFinite(c.Start) || !double.IsFinite(c.In) || !double.IsFinite(c.Out) || !double.IsFinite(c.SourceDuration) ||
                !double.IsFinite(c.Volume) || c.Start < 0 || c.In < 0 || c.Duration < MinDuration - 1e-8 ||
                c.End > 14400 || c.Volume is < 0 or > 2)
                throw new ArgumentException("Clip có thời gian hoặc âm lượng không hợp lệ (tối đa 4 giờ, âm lượng 0–2).");
            if (c.Track != TimelineTrack.Subtitle && (string.IsNullOrWhiteSpace(c.Path) || c.Out > c.SourceDuration + 0.02))
                throw new ArgumentException("Mốc cắt vượt thời lượng nguồn hoặc thiếu đường dẫn media.");
            if (c.Track == TimelineTrack.Video)
            {
                if (Math.Abs(c.Start - videoStart) > 0.001) throw new ArgumentException("Các clip video phải nối liên tiếp.");
                videoStart += c.Duration;
            }
            if (forRender && c.End > project.Duration + 0.02)
                throw new ArgumentException("Audio/phụ đề vượt cuối video; hãy rút ngắn hoặc di chuyển thành phần đó.");
        }
        if (project.Clips.Count(c => c.Track == TimelineTrack.Video) > 200) throw new ArgumentException("Tối đa 200 clip video.");
        if (forRender && project.Duration < MinDuration) throw new ArgumentException("Thêm ít nhất một clip video trước khi xem hoặc xuất.");
    }

    public static TimelineProject Replace(TimelineProject project, TimelineClip clip)
    {
        var result = Normalize(project with { Clips = project.Clips.Select(c => c.Id == clip.Id ? clip : c).ToArray() });
        Validate(result);
        return result;
    }

    public static TimelineProject Split(TimelineProject project, Guid id, double position)
    {
        var clip = project.Clips.First(c => c.Id == id);
        var offset = position - clip.Start;
        if (!double.IsFinite(position) || offset < MinDuration || clip.Duration - offset < MinDuration)
            throw new ArgumentException("Đặt playhead bên trong clip, cách mỗi đầu ít nhất 0,08 giây.");
        var result = new List<TimelineClip>();
        foreach (var c in project.Clips)
        {
            if (c.Id != id) { result.Add(c); continue; }
            result.Add(c with { Out = c.In + offset });
            result.Add(c with { Id = Guid.NewGuid(), Start = position, In = c.In + offset });
        }
        return Normalize(project with { Clips = result.ToArray() });
    }

    public static TimelineProject MoveVideo(TimelineProject project, Guid id, int index)
    {
        var video = project.Clips.Where(c => c.Track == TimelineTrack.Video).ToList();
        var clip = video.First(c => c.Id == id);
        video.Remove(clip);
        video.Insert(Math.Clamp(index, 0, video.Count), clip);
        return Normalize(project with { Clips = [.. video, .. project.Clips.Where(c => c.Track != TimelineTrack.Video)] });
    }
}

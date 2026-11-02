using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Shell;

namespace SuperMOOO.App.Features.S17;

[Service]
public sealed class TimelineNavigation(IServiceProvider services, ScreenRegistry screens, IMediaProbe probe, IDialogService dialogs)
{
    public async Task OpenAsync(TimelineProject project, string message)
    {
        var vm = services.GetRequiredService<TimelineViewModel>();
        if (!await vm.ReplaceProjectAsync(TimelineEditing.Normalize(project), saved: false)) return;
        services.GetRequiredService<MainViewModel>().Select(screens.Find("S17")!);
        dialogs.Info("Đã mở trong Timeline", message);
    }

    public async Task<TimelineClip> VideoAsync(string path, double? start = null, double? end = null, double volume = 1, bool muted = false)
    {
        var info = await probe.ProbeAsync(path, CancellationToken.None);
        if (info.Video is null) throw new ArgumentException("Nguồn không có video.");
        return TimelineClip.Media(TimelineTrack.Video, Path.GetFullPath(path), info.Duration.TotalSeconds) with
        { In = start ?? 0, Out = end ?? info.Duration.TotalSeconds, Volume = volume, Muted = muted };
    }

    public async Task AddAudioAsync(List<TimelineClip> clips, string? path, TimelineTrack track, double duration, double start, double volume, bool loop = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var info = await probe.ProbeAsync(path, CancellationToken.None);
        if (info.Audio is null || info.Duration.TotalSeconds < TimelineEditing.MinDuration) throw new ArgumentException("Nguồn không có audio hợp lệ.");
        if (start < 0 || start >= duration) throw new ArgumentException("Mốc audio nằm ngoài video.");
        do
        {
            var length = Math.Min(info.Duration.TotalSeconds, duration - start);
            if (length < TimelineEditing.MinDuration) break;
            clips.Add(TimelineClip.Media(track, Path.GetFullPath(path), info.Duration.TotalSeconds, start) with { Out = length, Volume = volume });
            start += length;
            if (clips.Count > 500) throw new ArgumentException("Audio lặp tạo quá nhiều clip; chọn file nhạc dài hơn.");
        } while (loop && start < duration);
    }
}

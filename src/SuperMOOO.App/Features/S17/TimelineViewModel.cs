using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.Media.Render;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Workflows.Timeline;

namespace SuperMOOO.App.Features.S17;

[Service]
public sealed partial class TimelineViewModel : JobViewModelBase
{
    private readonly IMediaProbe _probe;
    private readonly TimelineRenderer _renderer;
    private readonly AppPaths _paths;
    private readonly List<TimelineProject> _undo = [];
    private readonly List<TimelineProject> _redo = [];
    private TimelineProject _saved;
    private string? _preview;

    public TimelineViewModel(IMediaProbe probe, TimelineRenderer renderer, AppPaths paths, JobTracker tracker, IDialogService dialogs)
        : base(tracker, dialogs)
    {
        _probe = probe;
        _renderer = renderer;
        _paths = paths;
        _saved = Project;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsRunning)) OnPropertyChanged(nameof(CanEdit)); };
    }

    public ObservableCollection<TimelineClip> Clips { get; } = [];
    public static IReadOnlyList<string> Aspects { get; } = ["16:9", "9:16", "1:1", "Original"];
    public static IReadOnlyList<int> Heights { get; } = [360, 480, 720, 1080, 2160];
    [ObservableProperty] public partial TimelineProject Project { get; set; } = TimelineProject.Empty;
    [ObservableProperty] public partial TimelineClip? SelectedClip { get; set; }
    [ObservableProperty] public partial double Playhead { get; set; }
    [ObservableProperty] public partial double Zoom { get; set; } = 35;
    [ObservableProperty] public partial bool Snapping { get; set; } = true;
    [ObservableProperty] public partial string ClipIn { get; set; } = "0";
    [ObservableProperty] public partial string ClipOut { get; set; } = "0";
    [ObservableProperty] public partial string ClipPosition { get; set; } = "0";
    [ObservableProperty] public partial string ClipText { get; set; } = "";
    [ObservableProperty] public partial double ClipVolume { get; set; } = 1;
    [ObservableProperty] public partial bool ClipMuted { get; set; }
    [ObservableProperty] public partial string ProjectName { get; set; } = "Dự án mới";
    [ObservableProperty] public partial string Aspect { get; set; } = "16:9";
    [ObservableProperty] public partial int Height { get; set; } = 720;
    [ObservableProperty] public partial string? ProjectPath { get; set; }
    [ObservableProperty] public partial string PreviewStatus { get; set; } = "Tạo preview để xem hình, âm thanh và phụ đề đồng bộ.";
    public bool CanEdit => !IsRunning;
    public bool HasSelection => SelectedClip is not null;
    public bool NoSelection => SelectedClip is null;
    public bool IsMedia => SelectedClip is { Track: not TimelineTrack.Subtitle };
    public string MuteLabel => IsSubtitle ? "Ẩn phụ đề này" : "Tắt tiếng clip";
    public string VolumeLabel => $"Âm lượng: {ClipVolume:0.##}×";
    public bool CanPosition => SelectedClip is { Track: not TimelineTrack.Video };
    public bool IsSubtitle => SelectedClip?.Track == TimelineTrack.Subtitle;
    public double Duration => Project.Duration;
    public bool IsDirty => !ReferenceEquals(Project, _saved);
    public string ProjectTitle => Project.Name + (IsDirty ? " *" : "");
    public string Timing => $"{TimeSpan.FromSeconds(Math.Max(0, Playhead)):hh\\:mm\\:ss\\.fff} / {TimeSpan.FromSeconds(Duration):hh\\:mm\\:ss\\.fff}";
    public event EventHandler<string>? PreviewReady;
    public event EventHandler? PreviewInvalidated;
    public event EventHandler? SeekRequested;

    partial void OnPlayheadChanged(double value) => OnPropertyChanged(nameof(Timing));
    partial void OnSelectedClipChanged(TimelineClip? value)
    {
        ClipIn = Number(value?.In ?? 0); ClipOut = Number(value?.Out ?? 0); ClipPosition = Number(value?.Start ?? 0);
        ClipText = value?.Text ?? ""; ClipVolume = value?.Volume ?? 1; ClipMuted = value?.Muted ?? false;
        foreach (var name in new[] { nameof(HasSelection), nameof(NoSelection), nameof(CanPosition), nameof(IsSubtitle), nameof(IsMedia), nameof(MuteLabel) })
            OnPropertyChanged(name);
    }

    partial void OnClipVolumeChanged(double value) => OnPropertyChanged(nameof(VolumeLabel));

    private static string Number(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static double Parse(string value) => double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);

    private void Refresh(Guid? selection = null)
    {
        Clips.Clear(); foreach (var clip in Project.Clips) Clips.Add(clip);
        SelectedClip = Clips.FirstOrDefault(c => c.Id == selection);
        ProjectName = Project.Name; Aspect = Project.Aspect; Height = Project.Height;
        Playhead = Math.Clamp(Playhead, 0, Duration);
        foreach (var name in new[] { nameof(Duration), nameof(IsDirty), nameof(ProjectTitle), nameof(Timing) }) OnPropertyChanged(name);
        ClearPreview();
    }

    private void Commit(TimelineProject next, Guid? selection = null)
    {
        if (IsRunning) return;
        next = TimelineEditing.Normalize(next);
        TimelineEditing.Validate(next);
        _undo.Add(Project); if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear(); Project = next; Refresh(selection ?? SelectedClip?.Id);
    }

    private void Edit(Action action)
    {
        if (IsRunning) return;
        try { action(); }
        catch (Exception ex) when (IsExpected(ex)) { Dialogs.Error(ex.Message); }
    }

    private void ClearPreview()
    {
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        if (_preview is { } path)
        {
            try { File.Delete(path); }
            catch (IOException) { Status = "Preview cũ còn được sử dụng; cache sẽ được dọn sau."; }
            _preview = null;
        }
        PreviewStatus = "Preview cần cập nhật — bấm Tạo preview sau khi chỉnh sửa.";
    }

    public void Seek(double seconds)
    {
        Playhead = Math.Clamp(seconds, 0, Duration);
        SeekRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> ReplaceProjectAsync(TimelineProject project, bool saved = true)
    {
        if (IsRunning) { Dialogs.Error("Chờ tác vụ timeline hoàn tất hoặc huỷ trước."); return false; }
        TimelineEditing.Validate(project);
        if (IsDirty && !await Dialogs.ConfirmAsync("Thay dự án timeline?", "Dự án đang có thay đổi chưa lưu. Thay bằng dự án mới?", "Thay dự án")) return false;
        Project = project; _saved = saved ? project : TimelineProject.Empty; ProjectPath = null; _undo.Clear(); _redo.Clear(); Playhead = 0; Refresh();
        return true;
    }

    [RelayCommand] private Task NewAsync() => ReplaceProjectAsync(TimelineProject.Empty);
    [RelayCommand] private async Task OpenAsync()
    {
        if (IsRunning || Dialogs.OpenFile("Mở dự án timeline", "Timeline (*.aivtimeline)|*.aivtimeline") is not { } path) return;
        await GuardAsync(async () =>
        {
            var project = await TimelineProjectFile.LoadAsync(path);
            if (await ReplaceProjectAsync(project))
            {
                ProjectPath = path;
                var missing = project.Clips.Where(c => c.Track != TimelineTrack.Subtitle && !File.Exists(c.Path)).Select(c => c.Path).Distinct().Count();
                if (missing > 0) Dialogs.Error($"Thiếu {missing} file nguồn. Chọn clip và dùng Đổi nguồn để liên kết lại.");
            }
        });
    }
    [RelayCommand] private Task SaveAsync() => SaveProjectAsync(false);
    [RelayCommand] private Task SaveAsAsync() => SaveProjectAsync(true);
    private async Task SaveProjectAsync(bool saveAs)
    {
        if (IsRunning) return;
        var path = !saveAs && ProjectPath is not null ? ProjectPath : Dialogs.SaveFile("Lưu dự án timeline", "Timeline (*.aivtimeline)|*.aivtimeline", "project.aivtimeline");
        if (path is null) return;
        var snapshot = Project;
        if (await GuardAsync(() => TimelineProjectFile.SaveAsync(snapshot, path)))
        {
            ProjectPath = path; _saved = snapshot; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(ProjectTitle)); Status = "Đã lưu: " + path;
        }
    }

    [RelayCommand] private async Task AddMediaAsync(string kind)
    {
        if (!Enum.TryParse<TimelineTrack>(kind, out var track) || IsRunning) return;
        var filter = track == TimelineTrack.Video ? "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.ts" : "Âm thanh/video|*.wav;*.mp3;*.m4a;*.aac;*.flac;*.ogg;*.mp4;*.mkv";
        if (Dialogs.OpenFile("Thêm " + kind, filter + "|Tất cả|*.*") is { } path) await AddPathsAsync([path], track);
    }

    public async Task AddPathsAsync(IEnumerable<string> files, TimelineTrack track = TimelineTrack.Video)
    {
        if (IsRunning) return;
        TimelineProject? next = null;
        await RunDirectAsync(async (_, ct) =>
        {
            var list = Project.Clips.ToList();
            foreach (var path in files)
            {
                var info = await _probe.ProbeAsync(path, ct);
                if (track == TimelineTrack.Video ? info.Video is null : info.Audio is null) throw new ArgumentException("File không có luồng media phù hợp: " + path);
                var start = track == TimelineTrack.Video ? 0 : Playhead;
                var clip = TimelineClip.Media(track, Path.GetFullPath(path), info.Duration.TotalSeconds, start);
                if (track != TimelineTrack.Video)
                {
                    if (Duration - start < TimelineEditing.MinDuration) throw new ArgumentException("Thêm video và đặt playhead trước cuối video để chèn audio.");
                    clip = clip with { Out = Math.Min(clip.Out, Duration - start), Volume = track == TimelineTrack.Music ? 0.2 : 1 };
                }
                list.Add(clip);
            }
            next = TimelineEditing.Normalize(Project with { Clips = list.ToArray() });
            TimelineEditing.Validate(next);
        }, "Đọc media…");
        if (next is not null) Edit(() => Commit(next, next.Clips.LastOrDefault()?.Id));
    }

    [RelayCommand] private void AddSubtitle() => Edit(() =>
    {
        var length = Math.Min(3, Duration - Playhead);
        if (length < TimelineEditing.MinDuration) throw new ArgumentException("Đặt playhead trước cuối video để thêm phụ đề.");
        var clip = new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", Playhead, 0, length, 0, "Phụ đề mới");
        Commit(Project with { Clips = [.. Project.Clips, clip] }, clip.Id);
    });

    [RelayCommand] private async Task ImportSubtitlesAsync()
    {
        if (IsRunning || Dialogs.OpenFile("Nhập SRT", "SRT|*.srt") is not { } path) return;
        await GuardAsync(async () =>
        {
            var doc = SrtCodec.Parse(await File.ReadAllTextAsync(path));
            var clips = doc.Cues.Select(c => new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", c.Start.TotalSeconds, 0,
                (c.End - c.Start).TotalSeconds, 0, c.Text));
            Commit(Project with { Clips = [.. Project.Clips, .. clips] });
        });
    }

    [RelayCommand] private void ApplyClip() => Edit(() =>
    {
        if (SelectedClip is not { } clip) return;
        Commit(TimelineEditing.Replace(Project, clip with { In = Parse(ClipIn), Out = Parse(ClipOut),
            Start = clip.Track == TimelineTrack.Video ? clip.Start : Parse(ClipPosition), Text = ClipText, Volume = ClipVolume, Muted = ClipMuted }), clip.Id);
    });
    [RelayCommand] private void ApplyProject() => Edit(() => Commit(Project with { Name = ProjectName.Trim(), Aspect = Aspect, Height = Height }));
    [RelayCommand] private void Split() => Edit(() => { if (SelectedClip is { } clip) Commit(TimelineEditing.Split(Project, clip.Id, Playhead), clip.Id); });
    [RelayCommand] private void Delete() => Edit(() => { if (SelectedClip is { } clip) Commit(Project with { Clips = Project.Clips.Where(c => c.Id != clip.Id).ToArray() }); });
    [RelayCommand] private void MoveEarlier() => MoveSelected(-1);
    [RelayCommand] private void MoveLater() => MoveSelected(1);
    private void MoveSelected(int delta) => Edit(() =>
    {
        if (SelectedClip is not { Track: TimelineTrack.Video } clip) return;
        var index = Project.Clips.Where(c => c.Track == TimelineTrack.Video).ToList().FindIndex(c => c.Id == clip.Id);
        Commit(TimelineEditing.MoveVideo(Project, clip.Id, index + delta), clip.Id);
    });

    public void DragClip(Guid id, double delta, int edge) => Edit(() =>
    {
        var clip = Project.Clips.First(c => c.Id == id);
        if (edge == 0 && clip.Track == TimelineTrack.Video)
        {
            var video = Project.Clips.Where(c => c.Track == TimelineTrack.Video && c.Id != id).ToArray();
            var index = video.Count(c => c.Start + c.Duration / 2 < clip.Start + delta);
            Commit(TimelineEditing.MoveVideo(Project, id, index), id); return;
        }
        var destination = (edge == 1 ? clip.End : clip.Start) + delta;
        if (Snapping)
        {
            var targets = Project.Clips.Where(c => c.Id != id).SelectMany(c => new[] { c.Start, c.End }).Append(0).Append(Playhead);
            var nearest = targets.OrderBy(t => Math.Abs(t - destination)).First();
            if (Math.Abs(nearest - destination) < 8 / Zoom) destination = nearest;
        }
        var shift = destination - (edge == 1 ? clip.End : clip.Start);
        var updated = edge switch
        {
            -1 => clip with { In = Math.Clamp(clip.In + shift, 0, clip.Out - TimelineEditing.MinDuration),
                Start = clip.Track == TimelineTrack.Video ? clip.Start : Math.Max(0, clip.Start + Math.Clamp(shift, -clip.In, clip.Duration - TimelineEditing.MinDuration)) },
            1 => clip with { Out = Math.Clamp(clip.Out + shift, clip.In + TimelineEditing.MinDuration,
                clip.Track == TimelineTrack.Subtitle ? 14400 - clip.Start + clip.In : clip.SourceDuration) },
            _ => clip with { Start = Math.Max(0, destination) }
        };
        Commit(TimelineEditing.Replace(Project, updated), id);
    });

    [RelayCommand] private void Undo() => Edit(() =>
    {
        if (_undo.Count == 0) return;
        _redo.Add(Project); Project = _undo[^1]; _undo.RemoveAt(_undo.Count - 1); Refresh(SelectedClip?.Id);
    });
    [RelayCommand] private void Redo() => Edit(() =>
    {
        if (_redo.Count == 0) return;
        _undo.Add(Project); Project = _redo[^1]; _redo.RemoveAt(_redo.Count - 1); Refresh(SelectedClip?.Id);
    });

    [RelayCommand] private async Task RelinkAsync()
    {
        if (IsRunning || SelectedClip is not { Track: not TimelineTrack.Subtitle } clip || Dialogs.OpenFile("Đổi nguồn clip", "Media|*.*") is not { } path) return;
        await GuardAsync(async () =>
        {
            var info = await _probe.ProbeAsync(path, CancellationToken.None);
            if (clip.Track == TimelineTrack.Video ? info.Video is null : info.Audio is null) throw new ArgumentException("File không có luồng media phù hợp.");
            Commit(TimelineEditing.Replace(Project, clip with { Path = path, SourceDuration = info.Duration.TotalSeconds }), clip.Id);
        });
    }

    [RelayCommand] private async Task PreviewAsync()
    {
        if (IsRunning) return;
        ClearPreview();
        var path = Path.Combine(_paths.CacheDir("preview"), "timeline-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
        var snapshot = Project;
        if (await RunDirectAsync((p, ct) => _renderer.RenderAsync(snapshot, path, true, p, ct), "Tạo preview timeline…"))
        {
            _preview = path; PreviewStatus = "Preview đã cập nhật · 360p"; PreviewReady?.Invoke(this, path); Status = "Preview sẵn sàng.";
        }
    }

    [RelayCommand] private async Task ExportAsync()
    {
        if (IsRunning || Dialogs.SaveFile("Xuất timeline", "MP4|*.mp4", "timeline.mp4") is not { } path) return;
        if (await RunDirectAsync((p, ct) => _renderer.RenderAsync(Project, path, false, p, ct), "Xuất video timeline…"))
        { Status = "Đã xuất: " + path; Dialogs.Info("Xuất video hoàn tất", path); }
    }
}

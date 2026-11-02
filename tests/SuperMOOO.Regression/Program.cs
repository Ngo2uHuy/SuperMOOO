using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S10;
using SuperMOOO.App.Features.S17;
using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Models;
using SuperMOOO.Core.Operations;
using SuperMOOO.Core.Persistence;
using SuperMOOO.Infrastructure.Operations;
using SuperMOOO.Infrastructure.Processes;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Render;
using SuperMOOO.Social.Crawlers;
using SuperMOOO.Social.Listening;
using SuperMOOO.Social.Operations;
using SuperMOOO.Workflows.Timeline;

namespace SuperMOOO.Regression;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--ui")
        {
            try { UiChecks.Run(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory()); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        try { RunAsync(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory()).GetAwaiter().GetResult(); Console.WriteLine($"PASS: {_checks} checks"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++; Console.WriteLine("PASS: " + name);
    }
    private static async Task RejectAsync<T>(Func<Task> action, string name) where T : Exception
    {
        try { await action(); } catch (T) { Check(true, name); return; }
        throw new Exception("FAIL (expected rejection): " + name);
    }
    private static Task Reject<T>(Action action, string name) where T : Exception => RejectAsync<T>(() => { action(); return Task.CompletedTask; }, name);
    private static bool Near(double a, double b, double tolerance = 1e-6) => Math.Abs(a - b) < tolerance;

    private static async Task RunAsync(string repo)
    {
        repo = Path.GetFullPath(repo);
        var root = Path.Combine(repo, "artifacts", "timeline-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var redPath = Path.Combine(root, "nguồn đỏ.mp4");
        var bluePath = Path.Combine(root, "blue.mp4");
        var audioPath = Path.Combine(root, "tone.wav");
        var runner = new ProcessRunner();
        var tools = new TestTools(Path.Combine(repo, "tools"));
        var ffmpeg = new FfmpegRunner(runner, tools);
        var probe = new MediaProbe(runner, tools);
        var renderer = new TimelineRenderer(new ProjectRenderer(ffmpeg, probe), ffmpeg, probe, AppPaths.Create(root));
        var first = TimelineClip.Media(TimelineTrack.Video, redPath, 3) with { In = 0.25, Out = 1.25, Muted = true };
        var second = TimelineClip.Media(TimelineTrack.Video, bluePath, 3) with { In = 0.5, Out = 1.5, Muted = true };
        var music = TimelineClip.Media(TimelineTrack.Music, audioPath, 1, 0.5);
        var subtitle = new TimelineClip(Guid.NewGuid(), TimelineTrack.Subtitle, "", 0.4, 0, 0.5, 0, "Xin chào timeline");
        var project = TimelineEditing.Normalize(new TimelineProject(1, "Dự án kiểm thử", [first, second, music, subtitle], Height: 144));
        TimelineEditing.Validate(project, true);
        Check(Near(project.Duration, 2) && Near(project.Clips[1].Start, 1), "video ripple positions");
        var split = TimelineEditing.Split(project, first.Id, 0.375);
        Check(Near(split.Clips[0].Out, 0.625) && Near(split.Clips[1].In, 0.625) && Near(split.Duration, 2), "split preserves fractional source and duration");
        Check(split.Clips.Select(c => c.Id).Distinct().Count() == split.Clips.Count, "split creates unique IDs");
        Check(split.Clips.First(c => c.Id == music.Id) == music, "video split leaves audio placement intact");
        var reordered = TimelineEditing.MoveVideo(project, second.Id, 0);
        Check(reordered.Clips[0].Id == second.Id && Near(reordered.Clips[1].Start, 1), "video reorder preserves trims");
        var trimmed = TimelineEditing.Replace(project, first with { Out = 1 });
        Check(Near(trimmed.Clips[1].Start, 0.75) && Near(trimmed.Duration, 1.75), "trim ripples following video");
        await Reject<ArgumentException>(() => TimelineEditing.Split(project, first.Id, 0.01), "reject split at clip edge");
        await Reject<ArgumentException>(() => TimelineEditing.Replace(project, first with { In = double.NaN }), "reject nonfinite time");
        await Reject<ArgumentException>(() => TimelineEditing.Replace(project, first with { Out = 9 }), "reject trim beyond source");
        await Reject<ArgumentException>(() => TimelineEditing.Validate(project with { Version = 2 }), "reject unsupported project version");
        await Reject<ArgumentException>(() => TimelineEditing.Validate(project with { Clips = [first, first] }), "reject duplicate clip IDs");
        await Reject<ArgumentException>(() => TimelineEditing.Validate(project with { Clips = [.. project.Clips, subtitle with { Id = Guid.NewGuid(), Start = 10 }] }, true), "reject subtitle beyond output");
        var savedPath = Path.Combine(root, "dự án.aivtimeline");
        await TimelineProjectFile.SaveAsync(project, savedPath);
        var loaded = await TimelineProjectFile.LoadAsync(savedPath);
        Check(loaded.Name == project.Name && loaded.Clips.SequenceEqual(project.Clips), "save/load Unicode and complete clip state");
        var before = await File.ReadAllTextAsync(savedPath);
        await RejectAsync<ArgumentException>(() => TimelineProjectFile.SaveAsync(project with { Version = 2 }, savedPath), "invalid save rejected");
        Check(await File.ReadAllTextAsync(savedPath) == before, "failed save preserves previous project");
        var relative = project with { Clips = project.Clips.Select(c => c.Track == TimelineTrack.Subtitle ? c : c with { Path = Path.GetFileName(c.Path) }).ToArray() };
        await File.WriteAllTextAsync(savedPath, JsonSerializer.Serialize(relative));
        Check((await TimelineProjectFile.LoadAsync(savedPath)).Clips[0].Path == redPath, "relative sources resolve against project folder");
        await File.WriteAllTextAsync(savedPath, "{not-json}");
        await RejectAsync<InvalidDataException>(() => TimelineProjectFile.LoadAsync(savedPath), "malformed project handled");

        var dialogs = new TestDialogs();
        var vm = new TimelineViewModel(probe, renderer, AppPaths.Create(root), new JobTracker(null!), dialogs);
        await vm.ReplaceProjectAsync(project);
        vm.SelectedClip = vm.Clips[0]; vm.Playhead = 0.4; vm.SplitCommand.Execute(null);
        Check(vm.Clips.Count == 5 && vm.IsDirty, "VM split and dirty state");
        vm.UndoCommand.Execute(null);
        Check(vm.Clips.Count == 4 && !vm.IsDirty, "undo restores saved state");
        vm.RedoCommand.Execute(null);
        Check(vm.Clips.Count == 5 && vm.IsDirty, "redo restores split");
        vm.UndoCommand.Execute(null); vm.SelectedClip = vm.Clips[0]; vm.DeleteCommand.Execute(null); vm.RedoCommand.Execute(null);
        Check(vm.Clips.Count == 3, "new edit clears redo history");
        dialogs.Confirm = false;
        Check(!await vm.ReplaceProjectAsync(TimelineProject.Empty) && vm.Clips.Count == 3, "cancel replacement preserves unsaved work");
        Check(dialogs.Errors.Count == 0, "editing commands report no unexpected errors");

        var form = new FormModel(typeof(ChannelRequest), new ChannelRequest { Url = "@example" });
        var choice = form.Fields.Single(f => f.Meta.Name == nameof(ChannelRequest.ContentType));
        Check(choice.Meta.Kind == FieldKind.Choice && choice.Meta.Choices.SequenceEqual(["videos", "shorts", "streams"]), "content type is a fixed choice");
        Check(choice.Meta.ChoiceLabels?.Count == 3, "localized content type labels");
        choice.Text = "shorts";
        Check(form.TryBuild(out var request) && ((ChannelRequest)request!).ContentType == "shorts", "dropdown preserves API value");
        choice.Text = "wrong";
        Check(!form.TryBuild(out _), "invalid content type blocked");
        var registry = new OperationRegistry([typeof(ChannelRequest).Assembly]);
        var crawl = registry.All.Where(d => d.Group == OperationGroup.Crawl).ToArray();
        Check(crawl.Length > 20 && crawl.All(d => CrawlOperationGroups.All.Contains(CrawlOperationGroups.For(d))), "all crawler operations remain categorized");
        Check(CrawlOperationGroups.For(registry.Find("crawl.export_comments_excel")!) == "Bình luận", "comment export grouped with comments");
        Check(CrawlOperationGroups.For(registry.Find("social_listening.search_youtube_endpoint")!) == "Tìm kiếm & hashtag", "search grouped separately");
        Check(registry.Find("social_listening.get_quota_summary_endpoint") is null, "quota screen operation removed");
        var usage = new TestUsage();
        var httpHandler = new TestHttp();
        using var httpClient = new HttpClient(httpHandler);
        var youtube = new YouTubeDataApiClient(new CrawlHttp(new TestClients(httpClient)), new PlatformCredentials(new TestSecrets()), new QuotaService(usage));
        var channel = await youtube.GetChannelAsync("@example");
        Check(channel.Title == "Test" && usage.Added == 1 && usage.SumQueries == 0, "API request succeeds without consulting local daily limits");
        httpHandler.Status = HttpStatusCode.TooManyRequests;
        try { await youtube.GetChannelAsync("@example"); throw new Exception("Expected provider rate limit"); }
        catch (ProviderException ex) { Check(ex.Kind == ProviderErrorKind.RateLimited, "provider rate limits still handled"); }

        await ffmpeg.RunAsync(new FfmpegJob(["-f", "lavfi", "-i", "color=c=red:s=320x180:r=30:d=3", "-f", "lavfi", "-i", "sine=frequency=200:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"], redPath), null, default);
        await ffmpeg.RunAsync(new FfmpegJob(["-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30:d=3", "-f", "lavfi", "-i", "sine=frequency=300:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest"], bluePath), null, default);
        await ffmpeg.RunAsync(new FfmpegJob(["-f", "lavfi", "-i", "sine=frequency=880:duration=1", "-c:a", "pcm_s16le"], audioPath), null, default);
        var output = Path.Combine(root, "export.mp4");
        await renderer.RenderAsync(project, output, false, null, default);
        var result = await probe.ProbeAsync(output, default);
        Check(result.Video is { Height: 144 } && result.Audio is not null && Near(result.Duration.TotalSeconds, 2, 0.15), "real export has expected video/audio and duration");
        await AssertPixelAsync(0.2, true); await AssertPixelAsync(1.7, false);
        var pcm = Path.Combine(root, "output.pcm");
        await ffmpeg.RunAsync(new FfmpegJob(["-i", output, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le"], pcm), null, default);
        var samples = await File.ReadAllBytesAsync(pcm);
        double Energy(double start, double end)
        {
            var sum = 0d; var count = 0;
            for (var i = (int)(start * 8000); i < end * 8000 && i * 2 + 1 < samples.Length; i++) { sum += Math.Abs((double)BitConverter.ToInt16(samples, i * 2)); count++; }
            return sum / Math.Max(1, count);
        }
        Check(Energy(0.1, 0.3) < 20 && Energy(0.7, 1.2) > 100 && Energy(1.8, 1.95) < 20, "mute source audio and delayed music match timeline");
        var preview = Path.Combine(root, "preview.mp4");
        await renderer.RenderAsync(project, preview, true, null, default);
        var previewInfo = await probe.ProbeAsync(preview, default);
        Check(previewInfo.Video?.Height == 360 && Near(previewInfo.Duration.TotalSeconds, result.Duration.TotalSeconds, 0.08), "preview timing matches export");
        var sourceBefore = await File.ReadAllBytesAsync(redPath);
        await RejectAsync<ArgumentException>(() => renderer.RenderAsync(project, redPath, false, null, default), "prevent export over source");
        var sourceAfter = await File.ReadAllBytesAsync(redPath);
        Check(sourceBefore.SequenceEqual(sourceAfter), "source unchanged after rejected overwrite");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = Path.Combine(root, "cancelled.mp4");
        await RejectAsync<OperationCanceledException>(() => renderer.RenderAsync(project, cancelled, false, null, cancellation.Token), "render honors cancellation");
        Check(!File.Exists(cancelled) && !File.Exists(FfmpegRunner.PartialPath(cancelled)), "cancelled export leaves no output");
        Console.WriteLine("Artifacts: " + root);

        async Task AssertPixelAsync(double at, bool red)
        {
            var path = Path.Combine(root, $"frame-{at}.rgb");
            await ffmpeg.RunAsync(new FfmpegJob(["-ss", at.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", output, "-frames:v", "1", "-vf", "crop=2:2:0:0", "-pix_fmt", "rgb24", "-f", "rawvideo"], path), null, default);
            var rgb = await File.ReadAllBytesAsync(path);
            Check(red ? rgb[0] > 180 && rgb[2] < 30 : rgb[2] > 180 && rgb[0] < 30, "clip order at " + at);
        }
    }
}

internal sealed class TestTools(string folder) : IToolLocator
{
    public string Resolve(ToolKind tool) => Path.Combine(folder, tool == ToolKind.Ffmpeg ? "ffmpeg.exe" : "ffprobe.exe");
    public bool TryResolve(ToolKind tool, out string path) { path = Resolve(tool); return File.Exists(path); }
}
internal sealed class TestDialogs : IDialogService
{
    public bool Confirm { get; set; } = true;
    public List<string> Errors { get; } = [];
    public void Info(string title, string message) { }
    public void Error(string message) => Errors.Add(message);
    public Task<bool> ConfirmAsync(string title, string message, string confirmText = "", string cancelText = "") => Task.FromResult(Confirm);
    public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    public string? OpenFile(string title, string filter) => null;
    public string? SaveFile(string title, string filter, string defaultName) => null;
    public string? PickFolder(string title) => null;
}
internal sealed class TestUsage : IQuotaUsageStore
{
    public int Added { get; private set; }
    public int SumQueries { get; private set; }
    public Task AddAsync(string platform, string endpoint, int units, string caller = "system", CancellationToken ct = default) { Added += units; return Task.CompletedTask; }
    public Task<long> SumUnitsSinceAsync(string platform, DateTimeOffset since, CancellationToken ct = default) { SumQueries++; return Task.FromResult(long.MaxValue); }
}
internal sealed class TestSecrets : ISecretStore
{
    public Task<string?> GetAsync(SecretKey key, CancellationToken ct = default) => Task.FromResult<string?>("test-only");
    public Task SetAsync(SecretKey key, string value, string status = "ok", CancellationToken ct = default) => throw new NotSupportedException();
    public Task<bool> DeleteAsync(SecretKey key, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SecretInfo>> ListAsync(string? scopePrefix = null, CancellationToken ct = default) => throw new NotSupportedException();
}
internal sealed class TestClients(HttpClient client) : IHttpClientProvider { public HttpClient Get(string name) => client; }
internal sealed class TestHttp : HttpMessageHandler
{
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent("{\"items\":[{\"id\":\"test\",\"snippet\":{\"title\":\"Test\"}}]}"), RequestMessage = request });
}

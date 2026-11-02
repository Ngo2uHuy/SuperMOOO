using System.ComponentModel.DataAnnotations;
using SuperMOOO.AI.Tts;
using SuperMOOO.Media.Audio;
using SuperMOOO.Media.Ffmpeg;
using SuperMOOO.Media.Slides;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Talkshow;
using SuperMOOO.Workflows.Delivery;
using SuperMOOO.Workflows.Talkshow;
using F = SuperMOOO.Media.Ffmpeg.Fmt;

namespace SuperMOOO.Workflows.Steps;

public class SlideshowStepOptions
{
    [Range(360, 2160)] public int Height { get; set; } = 1080;
    public bool KenBurns { get; set; } = true;
    public bool Fade { get; set; } = true;
    public bool Waveform { get; set; } = true;
    [RegularExpression("#[0-9a-fA-F]{6}", ErrorMessage = "Màu sóng âm phải có dạng #RRGGBB.")] public string WaveformColor { get; set; } = "#00FFCC";
    [Range(15, 60)] public int Fps { get; set; } = 30;
}

public sealed class BackgroundVideoOptions : SlideshowStepOptions
{
    public BackgroundVideoOptions()
    {
        KenBurns = false;
        Waveform = false;
    }
}

internal static class SlideshowRun
{
    /// <summary>Video thân của slideshow. Còn bước sau (intro/outro, gắn phụ đề) thì ghi vào thư mục làm việc và đặt `finalPath`; không thì ghi thẳng ra thư mục kết quả.</summary>
    public static async Task<StepOutput> RunAsync(SlideshowRenderer renderer, IOutputRegistry outputs, StepContext c, string slide,
        SlideshowStepOptions o, CancellationToken ct)
    {
        var name = Io.Name(c);
        var final = Io.UniquePath(Io.OutputDir(c), name + ".mp4");
        var toWork = Io.Flag(c, "introOutro") || Io.Flag(c, "burn");
        var target = toWork ? Io.Work(c, name + ".mp4") : final;

        await renderer.RenderAsync(new SlideshowOptions(slide, Io.State(c, "dubAudio"), o.Height, o.KenBurns, o.Fade, o.Waveform, o.WaveformColor, Fps: o.Fps),
            target, c.Progress, ct);
        if (!toWork) await outputs.RegisterOutputAsync(final, "youtube", ct);
        return Io.Out([("video", target), ("finalPath", final)], target);
    }
}

/// <summary>Một ảnh/clip + giọng đọc → video (Ken Burns, sóng âm, fade). Slide lấy từ input.slide.</summary>
public sealed class SlideshowStep(SlideshowRenderer renderer, IOutputRegistry outputs) : PipelineStep<SlideshowStepOptions>
{
    public override string Kind => "slideshow";

    protected override Task<StepOutput> RunAsync(StepContext c, SlideshowStepOptions o, CancellationToken ct) =>
        SlideshowRun.RunAsync(renderer, outputs, c, Io.Require(c, "slide"), o, ct);
}

/// <summary>Mode `script` của Auto: dùng video có sẵn (vd xuất từ ArtCraft) làm nền thay slide ảnh; video lặp cho đủ thời lượng giọng.</summary>
public sealed class BackgroundVideoStep(SlideshowRenderer renderer, IOutputRegistry outputs) : PipelineStep<BackgroundVideoOptions>
{
    public override string Kind => "background_video";

    protected override Task<StepOutput> RunAsync(StepContext c, BackgroundVideoOptions o, CancellationToken ct) =>
        SlideshowRun.RunAsync(renderer, outputs, c, Io.Require(c, "backgroundVideo"), o, ct);
}

public sealed class VoiceSetting
{
    [Required] public string VoiceId { get; set; } = "cpp_vieneu_minh_quan_pro";
    [Range(0.7, 1.4)] public double Speed { get; set; } = 1.0;
}

public sealed class TalkshowStageOptions
{
    public VoiceSetting Host { get; set; } = new();
    public VoiceSetting Guest { get; set; } = new() { VoiceId = "cpp_vieneu_ngoc_huyen" };
    public string? Language { get; set; }
    [Required] public string Stage { get; set; } = "";
    [Required] public string Scenery { get; set; } = "";
    public string? Music { get; set; }
    [Range(0.0, 0.5)] public double MusicVolume { get; set; } = 0.06;
    [Range(720, 1080)] public int Height { get; set; } = 720;
    public bool Captions { get; set; } = true;
    public bool ScreenKey { get; set; } = true;
}

/// <summary>Talkshow: TTS hai giọng theo lượt (nghỉ giữa lượt), ghép thành một track hội thoại rồi dựng sân khấu key xanh + nhạc nền + phụ đề.</summary>
public sealed class TalkshowStageStep(TtsService tts, FfmpegRunner ffmpeg, DubTrackAssembler assembler, StageCompositor stage, IOutputRegistry outputs)
    : PipelineStep<TalkshowStageOptions>
{
    private const int CaptionWords = 14;

    public override string Kind => "talkshow_stage";

    protected override async Task<StepOutput> RunAsync(StepContext c, TalkshowStageOptions o, CancellationToken ct)
    {
        var turns = (c.Input["turns"] as JsonArray)?.Deserialize<List<TalkshowTurn>>(PipelineResult.Json) ?? [];
        TalkshowScript.Validate(turns);

        var dir = Path.Combine(c.WorkFolder, "talk");
        Directory.CreateDirectory(dir);
        var segments = new List<DubSegment>();
        var cues = new List<SubtitleCue>();
        var cursor = 0.0;

        for (var i = 0; i < turns.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var turn = turns[i];
            var host = turn.Role == TalkshowScript.Host;
            var voice = host ? o.Host : o.Guest;
            var raw = Path.Combine(dir, $"turn_{i:0000}.wav");
            await tts.SynthesizeAsync(new TtsRequest(turn.Text, voice.VoiceId, raw, o.Language), ct);

            var path = raw;
            if (Math.Abs(voice.Speed - 1) > 0.001)
            {
                path = Path.Combine(dir, $"turn_{i:0000}.speed.wav");
                await ffmpeg.RunAsync(new FfmpegJob(["-i", raw, "-af", F.TempoChain(voice.Speed), "-c:a", "pcm_s16le"], path, Label: "Tốc độ lượt thoại"), null, ct);
            }

            var speaking = WavFile.DescribeFile(path).Duration.TotalSeconds;
            if (speaking <= 0.05) throw new InvalidOperationException($"Lượt thoại {i + 1} không có âm thanh hợp lệ.");
            segments.Add(new DubSegment(path, TimeSpan.FromSeconds(cursor)));
            cues.AddRange(Captions(turn, host ? "MC: " : "Khách: ", cursor, speaking, cues.Count));
            cursor += speaking + turn.Pause;
            c.Progress.Report(new ProgressInfo(0.5 * (i + 1) / turns.Count, $"lượt {i + 1}/{turns.Count}"));
        }

        var dialogue = Io.Work(c, "dialogue.wav");
        await assembler.AssembleAsync(segments, TimeSpan.FromSeconds(cursor), dialogue, AudioMixer.SampleRate, null, ct);

        var document = new SubtitleDocument(cues);
        var name = Io.Name(c);
        var video = Io.UniquePath(Io.OutputDir(c), name + ".mp4");
        await stage.RenderAsync(new StageOptions(dialogue, o.Stage, o.Scenery, o.Captions ? document : null,
            string.IsNullOrWhiteSpace(o.Music) ? null : o.Music, o.MusicVolume, o.Height, o.ScreenKey), video, c.Progress, ct);

        var srt = Path.ChangeExtension(video, ".srt");
        await document.SaveAsync(srt, new SrtFormat(), ct);
        await outputs.RegisterOutputAsync(video, "talkshow", ct);
        return Io.Out([("video", video), ("subtitleFile", srt), ("dialogueSeconds", Io.Fmt(cursor))], video, srt);
    }

    /// <summary>Chia lượt thoại thành các dòng ~14 từ, thời gian chia theo số từ trong thời lượng đọc.</summary>
    private static IEnumerable<SubtitleCue> Captions(TalkshowTurn turn, string label, double start, double speaking, int index)
    {
        var words = turn.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var pieces = words.Chunk(CaptionWords).Select(w => string.Join(' ', w)).ToList();
        var offset = start;
        for (var k = 0; k < pieces.Count; k++)
        {
            var end = offset + speaking * pieces[k].Split(' ').Length / words.Length;
            yield return new SubtitleCue(index + k + 1, TimeSpan.FromSeconds(offset), TimeSpan.FromSeconds(end), label + pieces[k]);
            offset = end;
        }
    }
}

/// <summary>Đưa video kết quả vào lịch đăng (pub_publications, origin auto_workflow). Chạy lại không tạo bài trùng nhờ plan_key.</summary>
public sealed class DeliverStep(DeliveryService deliveries) : PipelineStep<DeliveryOptions>
{
    public override string Kind => "deliver";

    protected override async Task<StepOutput> RunAsync(StepContext c, DeliveryOptions o, CancellationToken ct)
    {
        var files = (Io.StateOrNull(c, "videos") ?? Io.State(c, "video")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Video có SEO riêng (whiteboard) thì điền tiêu đề/mô tả khi người dùng không nhập.
        if (o.Title.Length == 0 && Io.StateOrNull(c, "seoTitle") is { } seoTitle) o.Title = seoTitle.Length > 100 ? seoTitle[..100] : seoTitle;
        if (o.Caption.Length == 0 && Io.StateOrNull(c, "seoCaption") is { } seoCaption) o.Caption = seoCaption.Length > 5000 ? seoCaption[..5000] : seoCaption;
        var created = await deliveries.EnqueueAsync(c.JobId, files, o, Io.Name(c), ct);
        return Io.Out([("deliveries", created.ToString(CultureInfo.InvariantCulture))]);
    }
}

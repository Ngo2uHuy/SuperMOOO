using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Audio;

/// <param name="MaxTempo">Trần atempo (bản cũ clamp 1.8).</param>
/// <param name="BorrowGapSeconds">Tối đa mượn khoảng lặng trước câu kế.</param>
/// <param name="MinTempo">1.0 = không bao giờ kéo chậm; 0.9 = cho phép chậm tối đa 10%.</param>
/// <param name="Tolerance">Lệch trong ±5% thì không atempo.</param>
/// <param name="AllowResynth">Provider hỗ trợ Rate: cho phép yêu cầu tổng hợp lại 1 lần.</param>
public sealed record TempoOptions(double MaxTempo = 1.8, double BorrowGapSeconds = 0.6, double MinTempo = 1.0,
    double Tolerance = 0.05, bool AllowResynth = false);

public enum TempoAction { PadOnly, Keep, Atempo, Resynthesize, Truncate }

/// <param name="Tempo">Hệ số atempo sẽ áp (1 = không).</param>
/// <param name="OutputSeconds">Độ dài tiếng sau xử lý (trước khi đệm).</param>
/// <param name="LimitSeconds">Trần thời gian cho phép (slot + phần mượn).</param>
/// <param name="ResynthRateFactor">Với Resynthesize: nhân Rate của provider với hệ số này rồi gọi Plan lại (AllowResynth = false).</param>
public sealed record TempoPlan(TempoAction Action, double Tempo, double OutputSeconds, double BorrowedSeconds,
    double LimitSeconds, double ResynthRateFactor = 1, string? Warning = null);

/// <summary>
/// Khớp thời lượng câu TTS với ô thoại (F26, thay hồi quy atempo không giới hạn của Craw):
/// không kéo chậm câu ngắn, bỏ qua lệch nhỏ, mượn khoảng lặng, rồi mới atempo (kẹp 1.0–MaxTempo), cuối cùng cắt + fade-out.
/// </summary>
[Service]
public sealed class TempoFitter(FfmpegRunner ffmpeg)
{
    private const double FadeSeconds = 0.08;

    public static TempoPlan Plan(double ttsSeconds, double slotSeconds, double gapAfterSeconds, TempoOptions? options = null)
    {
        var o = options ?? new TempoOptions();
        if (!double.IsFinite(ttsSeconds) || !double.IsFinite(slotSeconds) || ttsSeconds <= 0 || slotSeconds <= 0)
            throw new ArgumentException("Thời lượng TTS và ô thoại phải hữu hạn và lớn hơn 0.");

        var r = ttsSeconds / slotSeconds;
        if (r <= 1)
        {
            var slow = Math.Clamp(r, Math.Min(o.MinTempo, 1), 1);
            return slow < 1
                ? new TempoPlan(TempoAction.Atempo, slow, ttsSeconds / slow, 0, slotSeconds)
                : new TempoPlan(TempoAction.PadOnly, 1, ttsSeconds, 0, slotSeconds);
        }

        var gap = Math.Clamp(gapAfterSeconds, 0, o.BorrowGapSeconds);
        var limit = slotSeconds + gap;
        if (r <= 1 + o.Tolerance)
            return new TempoPlan(TempoAction.Keep, 1, ttsSeconds, ttsSeconds - slotSeconds, Math.Max(limit, ttsSeconds));

        var r2 = ttsSeconds / limit;
        if (r2 <= 1 + o.Tolerance)
            return new TempoPlan(TempoAction.Keep, 1, ttsSeconds, Math.Max(0, ttsSeconds - slotSeconds), limit);

        var tempo = Math.Clamp(r2, 1, o.MaxTempo);
        var fitted = ttsSeconds / tempo;
        if (fitted <= limit + 0.01)
            return new TempoPlan(TempoAction.Atempo, tempo, fitted, Math.Max(0, fitted - slotSeconds), limit);

        if (o.AllowResynth)
            return new TempoPlan(TempoAction.Resynthesize, tempo, fitted, 0, limit, ResynthRateFactor: r2 / o.MaxTempo);

        return new TempoPlan(TempoAction.Truncate, tempo, limit, Math.Max(0, limit - slotSeconds), limit,
            Warning: $"Câu TTS dài {ttsSeconds:0.00}s vượt ô {limit:0.00}s dù đã tăng tốc {tempo:0.##}x: đã cắt bớt.");
    }

    /// <summary>Áp kế hoạch lên file WAV: atempo, cắt + fade-out khi Truncate, đệm im lặng tới hết ô khi cần. extraTempo gộp tốc độ đọc chưa áp vào cùng một lần ffmpeg.</summary>
    public async Task ApplyAsync(string input, string output, TempoPlan plan, double slotSeconds, CancellationToken ct, double extraTempo = 1)
    {
        var chain = new List<string>();
        var tempo = (plan.Action is TempoAction.Atempo or TempoAction.Truncate ? plan.Tempo : 1) * extraTempo;
        if (Math.Abs(tempo - 1) > 1e-6) chain.Add(Fmt.TempoChain(tempo));
        if (plan.Action == TempoAction.Truncate)
            chain.Add($"atrim=duration={Fmt.N(plan.LimitSeconds)},afade=t=out:st={Fmt.N(Math.Max(0, plan.LimitSeconds - FadeSeconds))}:d={FadeSeconds}");
        if (plan.Action is TempoAction.PadOnly or TempoAction.Atempo or TempoAction.Truncate)
            chain.Add($"apad=whole_dur={Fmt.N(slotSeconds)}");
        if (chain.Count == 0) chain.Add("anull");

        await ffmpeg.RunAsync(new FfmpegJob(["-i", input, "-af", string.Join(',', chain), "-c:a", "pcm_s16le"], output,
            TimeSpan.FromSeconds(Math.Max(slotSeconds, plan.OutputSeconds)), Label: "Khớp thời lượng"), null, ct);
    }
}

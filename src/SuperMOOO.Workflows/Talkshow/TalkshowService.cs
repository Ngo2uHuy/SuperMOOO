using System.Text.RegularExpressions;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;
using SuperMOOO.Media.Talkshow;
using SuperMOOO.Workflows.Projects;
using SuperMOOO.Workflows.Steps;

namespace SuperMOOO.Workflows.Talkshow;

/// <summary>Một lượt thoại. Role = "host" (MC) hoặc "guest" (khách mời); Pause là khoảng nghỉ sau lượt (giây).</summary>
public sealed record TalkshowTurn(string Role, string Text, double Pause = 0.35);

public static partial class TalkshowScript
{
    public const string Host = "host";
    public const string Guest = "guest";
    public const int MaxTurns = 400;

    [GeneratedRegex(@"^(MC|HOST|NGƯỜI DẪN|KHÁCH MỜI|KHÁCH|GUEST)\s*:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();

    /// <summary>Mỗi đoạn một người nói, bắt đầu bằng "MC:" hoặc "KHÁCH:"; dòng ngắt giữa chừng thuộc về lượt trước.</summary>
    public static IReadOnlyList<TalkshowTurn> Parse(string text)
    {
        var turns = new List<TalkshowTurn>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Marker().Match(line) is { Success: true } m)
                turns.Add(new TalkshowTurn(m.Groups[1].Value.ToUpperInvariant() is "MC" or "HOST" or "NGƯỜI DẪN" ? Host : Guest, m.Groups[2].Value.Trim()));
            else if (turns.Count > 0) turns[^1] = turns[^1] with { Text = turns[^1].Text + " " + line };
            else throw new FormatException("Mỗi lượt thoại phải bắt đầu bằng MC: hoặc KHÁCH:");
        }

        Validate(turns);
        return turns;
    }

    public static string Format(IEnumerable<TalkshowTurn> turns) =>
        string.Join("\n\n", turns.Select(t => (t.Role == Host ? "MC" : "KHÁCH") + ": " + t.Text));

    public static void Validate(IReadOnlyList<TalkshowTurn> turns)
    {
        if (turns.Count < 2) throw new FormatException("Kịch bản cần ít nhất một lượt MC: và một lượt KHÁCH:");
        if (turns.Count > MaxTurns) throw new FormatException($"Kịch bản vượt {MaxTurns} lượt thoại; giảm thời lượng hoặc tăng độ dài mỗi lượt.");
        if (turns.Any(t => string.IsNullOrWhiteSpace(t.Text) || t.Text.Length > 5000 || t.Pause is < 0 or > 3))
            throw new FormatException("Mỗi lượt thoại cần có chữ (tối đa 5.000 ký tự) và nghỉ từ 0 đến 3 giây.");
        if (turns.Select(t => t.Role).Distinct().Count() != 2) throw new FormatException("Kịch bản cần ít nhất một lượt MC: và một lượt KHÁCH:");
    }

    /// <summary>Kịch bản mẫu offline ("Nạp kịch bản mẫu"): hư cấu, không dùng tên người/kênh có thật.</summary>
    public static IReadOnlyList<TalkshowTurn> Demo() => Parse("""
        MC: Chào bạn. Có những khoảng cách không nằm ở số cây số, mà nằm ở một cuộc gọi chúng ta cứ hẹn để ngày mai. Hôm nay, bạn muốn kể câu chuyện nào của mình?

        KHÁCH: Tôi từng nghĩ rằng gửi tiền về mỗi tháng là đã chăm sóc mẹ đủ rồi. Nhưng một tối, tôi gọi và thấy mẹ ngập ngừng hỏi: cuối tuần này con có bận không? Tôi vẫn trả lời như mọi lần, rằng công việc nhiều lắm.

        MC: Điều gì khiến bạn nhận ra mẹ cần một điều khác?

        KHÁCH: Em gái gửi cho tôi tấm ảnh cuốn lịch trên bàn. Mẹ khoanh từng ngày cuối tuần, rồi gạch đi khi tôi báo không về. Nhìn những dấu gạch ấy, tôi mới hiểu rằng mẹ đang đếm thời gian được gặp con, chứ không đếm những khoản tiền.

        MC: Sau đó, bạn đã thay đổi điều gì?

        KHÁCH: Sáng chủ nhật, tôi về mà không báo trước. Mẹ chẳng hỏi công việc hay thu nhập, chỉ kéo ghế bảo tôi ngồi xuống ăn sáng. Từ hôm ấy, mỗi tối tôi dành mười phút gọi về. Có hôm chúng tôi chỉ nói chuyện cây hoa ngoài hiên, nhưng tôi nghe được tiếng mẹ cười.

        MC: Có lẽ quan tâm không luôn cần một việc thật lớn. Đôi khi, đó là giữ một lời hẹn nhỏ và thực sự lắng nghe. Cảm ơn bạn đã chia sẻ. Nếu hôm nay bạn đang nhớ một người, hãy dành cho họ một cuộc gọi.
        """);
}

/// <summary>Cấu hình dựng talkshow (S03 tab Talkshow). Stage/Scenery trống = ảnh mẫu đi kèm app.</summary>
public sealed class TalkshowProject
{
    public string Title { get; set; } = "Chuyện bên tách trà";
    public string Topic { get; set; } = TalkshowService.Topics[0];
    public string Prompt { get; set; } = "";
    public string Language { get; set; } = "vi";
    public List<TalkshowTurn> Turns { get; set; } = [];
    public VoiceSetting Host { get; set; } = new();
    public VoiceSetting Guest { get; set; } = new() { VoiceId = "cpp_vieneu_ngoc_huyen" };
    public string Stage { get; set; } = "";
    public string Scenery { get; set; } = "";
    public string Music { get; set; } = "";
    public double MusicVolume { get; set; } = 0.06;
    public int Height { get; set; } = 720;
    public bool Captions { get; set; } = true;
    public bool ScreenKey { get; set; } = true;
    public string? OutputFolder { get; set; }
}

/// <summary>S03 Talkshow: kịch bản AI (chia phần 3 phút để nối mạch và có điểm huỷ) hoặc mẫu offline, 2 giọng, sân khấu, xem trước, xuất qua pipeline `talkshow`.</summary>
[Service]
public sealed class TalkshowService(LlmProviderRegistry llm, StageCompositor stage, IJobQueue jobs, ProjectIndex projects, AppPaths paths)
{
    public static readonly IReadOnlyList<string> Topics =
    [
        "Gia đình & tình thân", "Tuổi già & con cái", "Hôn nhân & tình yêu", "Mâu thuẫn & hòa giải", "Nghị lực & cuộc sống", "Chủ đề tự nhập"
    ];

    private const int MinutesPerPart = 3;

    public static string AssetsFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "talkshow");
    public static string DefaultStage => Path.Combine(AssetsFolder, "studio.png");
    public static string DefaultScenery => Path.Combine(AssetsFolder, "coast.png");

    public async Task<IReadOnlyList<TalkshowTurn>> GenerateScriptAsync(string topic, string prompt, int minutes, string language,
        IProgress<ProgressInfo>? progress, CancellationToken ct, string? providerId = null, string? model = null)
    {
        if (string.IsNullOrWhiteSpace(topic) && string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Chọn chủ đề hoặc nhập prompt.");
        if (topic == Topics[^1] && string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Nhập prompt cho chủ đề tự chọn.");
        if (minutes is < 1 or > 90) throw new ArgumentException("Thời lượng kịch bản từ 1 đến 90 phút.");

        var parts = (minutes + MinutesPerPart - 1) / MinutesPerPart;
        var turns = new List<TalkshowTurn>();
        var provider = string.IsNullOrWhiteSpace(providerId) ? llm.Selected : llm.Get(providerId.Trim());
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        for (var i = 0; i < parts; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new ProgressInfo((double)i / parts, $"AI viết phần {i + 1}/{parts}…"));
            var length = Math.Min(MinutesPerPart, minutes - i * MinutesPerPart);
            var context = turns.Count == 0 ? "" : TalkshowScript.Format(turns.TakeLast(4));
            var instruction =
                $"Viết phần {i + 1}/{parts} của một kịch bản talkshow hư cấu mới bằng {LanguageCatalog.TextLanguage(language)}. " +
                $"Phần này khoảng {length} phút, {length * 210} từ. Tổng chương trình {minutes} phút. Chủ đề: {topic}. Yêu cầu của người dùng: {prompt}. " +
                "MC hỏi chuyện một khách mời. Diễn biến cụ thể, câu hỏi tự nhiên, chi tiết đời thường, cảm xúc tiết chế. " +
                (i == 0 ? "Mở đầu thu hút, giới thiệu câu chuyện. " : "Tiếp nối câu chuyện, giữ nguyên nhân vật và các chi tiết đã kể; không chào lại khán giả. ") +
                (i == parts - 1 ? "Kết thúc có sự thấu hiểu và lời chào ngắn. " : "Chưa kết thúc câu chuyện; mở đường cho phần tiếp theo. ") +
                "Không dùng tên, danh tính hay lời thoại của MC hoặc kênh có thật. Không khẳng định câu chuyện là có thật. " +
                "Chỉ trả về đối thoại; mỗi đoạn bắt đầu đúng MC: hoặc KHÁCH:. Không tiêu đề, không markdown, không chỉ dẫn sân khấu. MC nói ngắn, khách kể dài hơn. " +
                "Khách kể ở ngôi thứ nhất, tự xưng tôi; MC xưng tôi và gọi khách là bạn. Câu văn tự nhiên khi đọc, ngắt đoạn mỗi 2 đến 4 câu. " +
                (context.Length > 0 ? "Các đoạn gần nhất để nối mạch:\n" + context[^Math.Min(3500, context.Length)..] : "");
            var text = (await provider.CompleteAsync(new LlmRequest([new ChatMessage(ChatRole.User, instruction)], Model: model, MaxTokens: 8192, Temperature: 0.8), ct)).Text;
            turns.AddRange(TalkshowScript.Parse(text));
            if (turns.Count > TalkshowScript.MaxTurns) throw new FormatException($"Kịch bản vượt {TalkshowScript.MaxTurns} lượt thoại; giảm thời lượng.");
        }

        return turns;
    }

    /// <summary>Một khung sân khấu (PNG) để xem trước bố cục; trống = ảnh mẫu.</summary>
    public async Task<string> PreviewFrameAsync(string? stagePath, string? sceneryPath, bool screenKey, CancellationToken ct)
    {
        var folder = paths.CacheDir("frames");
        Directory.CreateDirectory(folder);
        var output = Path.Combine(folder, $"talkshow-{Guid.NewGuid():N}.png");
        await stage.PreviewFrameAsync(Pick(stagePath, DefaultStage), Pick(sceneryPath, DefaultScenery), screenKey, 360, output, ct);
        return output;
    }

    public async Task<string> StartAsync(TalkshowProject p, CancellationToken ct = default, string? requestId = null)
    {
        TalkshowScript.Validate(p.Turns);
        var stagePath = Pick(p.Stage, DefaultStage);
        var scenery = Pick(p.Scenery, DefaultScenery);
        if (!string.IsNullOrWhiteSpace(p.Music) && !File.Exists(p.Music)) throw new FileNotFoundException("Không tìm thấy nhạc nền.", p.Music);

        var name = SafeName(p.Title);
        var folder = p.OutputFolder is { Length: > 0 } f ? f : Path.Combine(projects.OutputRoot(), "Talkshow");
        await projects.SaveAsync(ProjectIndex.Talkshow, p.Title, Path.Combine(folder, name), s => s["project"] = JsonSerializer.SerializeToNode(p, PipelineResult.Json), ct);

        var input = new JsonObject
        {
            ["turns"] = PipelineResult.Node(p.Turns),
            ["outputDir"] = folder,
            ["name"] = name,
            ["stepOptions"] = new JsonObject
            {
                ["stage"] = PipelineResult.Node(new TalkshowStageOptions
                {
                    Host = p.Host, Guest = p.Guest, Language = p.Language, Stage = stagePath, Scenery = scenery,
                    Music = p.Music, MusicVolume = p.MusicVolume, Height = p.Height, Captions = p.Captions, ScreenKey = p.ScreenKey
                })
            }
        };
        if (requestId is not null) input["requestId"] = requestId;
        return await jobs.EnqueueAsync(new JobRequest(JobTypes.Pipeline, $"Talkshow: {p.Title}", PipelineResult.Pipeline("talkshow", input), JobLane.Media), ct);
    }

    private static string Pick(string? chosen, string fallback)
    {
        var path = string.IsNullOrWhiteSpace(chosen) ? fallback : chosen;
        return File.Exists(path) ? path : throw new FileNotFoundException($"Chưa có hình/video sân khấu: {path}", path);
    }

    private static string SafeName(string title)
    {
        var clean = new string(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "talkshow" : clean.Length > 80 ? clean[..80] : clean;
    }
}

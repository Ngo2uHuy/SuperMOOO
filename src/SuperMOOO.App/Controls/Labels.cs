using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Controls;

/// <summary>Nhãn tiếng Việt cho trạng thái kỹ thuật (job, bài đăng, video của Auto Reup) hiển thị trong bảng.</summary>
public static class Labels
{
    public static string Job(JobStatus status) => status switch
    {
        JobStatus.Queued => "Đang chờ",
        JobStatus.Running => "Đang chạy",
        JobStatus.Succeeded => "Xong",
        JobStatus.Failed => "Lỗi",
        JobStatus.Cancelled => "Đã huỷ",
        JobStatus.WaitingManualRetry => "Chờ bạn thử lại",
        _ => status.ToString()
    };

    public static string ReupItem(string status) => status switch
    {
        ReupItemStatus.Pending => "Chờ xử lý",
        ReupItemStatus.Processing => "Đang xử lý",
        ReupItemStatus.Ready => "Đã xử lý",
        ReupItemStatus.Failed => "Lỗi xử lý",
        ReupItemStatus.Ignored => "Đã bỏ qua",
        ReupItemStatus.Duplicate => "Đã có ở luồng khác",
        _ => status
    };

    public static string Format(DownloadFormat format) => format switch
    {
        DownloadFormat.Best => "Tốt nhất",
        DownloadFormat.Best4K => "Tốt nhất (tối đa 4K)",
        DownloadFormat.P1080 => "1080p",
        DownloadFormat.P720 => "720p",
        DownloadFormat.P480 => "480p",
        DownloadFormat.AudioBest => "Chỉ âm thanh",
        DownloadFormat.VideoMuted => "Video không tiếng",
        _ => format.ToString()
    };

    /// <summary>Loại bản ghi trong kho file (cột "Loại").</summary>
    public static string FileKind(string kind) => kind switch
    {
        "download" => "Tải về",
        "output" => "Kết quả",
        "publish" => "Đăng bài",
        _ => kind
    };

    /// <summary>Cột "Định dạng/Nguồn gốc": tên định dạng tải hoặc loại tác vụ đã sinh ra file.</summary>
    public static string FileOperation(string? operation) => operation switch
    {
        null or "" => "",
        "audio" => "Âm thanh",
        "youtube" => "Video YouTube",
        "talkshow" => "Talkshow",
        "whiteboard" => "Video vẽ tay",
        "lipsync" => "Avatar khớp môi",
        "review" => "Review phim",
        "subtitles" => "Phụ đề",
        "localize" => "Lồng tiếng",
        "reup" => "Reup",
        "download" => "Tải về",
        _ => Enum.TryParse<DownloadFormat>(operation, out var format) ? Format(format) : operation
    };

    private static readonly Dictionary<string, string> Terms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["off"] = "Tắt", ["fast"] = "Nhanh", ["ai"] = "AI",
        ["roformer"] = "Chất lượng cao (RoFormer)", ["mdx"] = "Nhanh (MDX Voc_FT)",
        ["original"] = "Giữ nguyên", ["contain"] = "Vừa khung (có viền)", ["cover"] = "Phủ kín khung (cắt bớt)",
        ["private"] = "Riêng tư", ["unlisted"] = "Không công khai", ["public"] = "Công khai",
        ["all"] = "Tất cả", ["channel"] = "Kênh", ["profile"] = "Hồ sơ", ["hashtag"] = "Hashtag", ["search"] = "Tìm kiếm",
        ["views"] = "Lượt xem", ["likes"] = "Lượt thích", ["comments"] = "Bình luận", ["followers"] = "Người theo dõi", ["shares"] = "Lượt chia sẻ",
        ["tiktok"] = "TikTok", ["youtube"] = "YouTube", ["facebook"] = "Facebook", ["instagram"] = "Instagram", ["douyin"] = "Douyin",
        ["xiaohongshu"] = "Xiaohongshu", ["weibo"] = "Weibo", ["bilibili"] = "Bilibili", ["kuaishou"] = "Kuaishou",
        ["twitter"] = "Twitter", ["threads"] = "Threads", ["meta"] = "Meta (Facebook/Instagram)",
        ["native.vieneu"] = "VieNeu (chạy máy)", ["native.omnivoice"] = "OmniVoice (chạy máy)", ["native.supertonic"] = "Supertonic (chạy máy)",
        ["native.qwen3"] = "Qwen3-TTS (chạy máy)", ["native.cosyvoice3"] = "CosyVoice3 (chạy máy)", ["native.irodori"] = "Irodori (chạy máy)",
        ["native.chatterbox"] = "Chatterbox (chạy máy)", ["native.breeze"] = "Breeze TTS 2 (chạy máy)", ["edge"] = "Microsoft Edge",
        ["openai"] = "OpenAI", ["gemini"] = "Gemini", ["elevenlabs"] = "ElevenLabs",
        ["google"] = "Google Cloud TTS",
        ["avatar_audio"] = "Lời thoại", ["lipsync"] = "Khớp môi (MuseTalk)", ["lipsync_finalize"] = "Xuất video avatar", ["lipsync_prepare"] = "Dò mặt avatar",
        ["topic_script"] = "Viết kịch bản", ["scene_images"] = "Tạo ảnh", ["scene_tts"] = "Thu âm TTS", ["whiteboard_render"] = "Vẽ tay & hiệu ứng", ["whiteboard_finalize"] = "Xuất bản",
        ["anthropic"] = "Anthropic (Claude)", ["openai_compat"] = "OpenAI-compatible (URL tuỳ chỉnh)", ["local"] = "Máy chủ cục bộ (Ollama/LM Studio)", ["bundled"] = "Qwen3 1.7B (chạy máy)",
        ["landscape"] = "Ngang 16:9", ["portrait"] = "Dọc 9:16",
        ["pollinations"] = "Pollinations (miễn phí)", ["muse"] = "Muse Image (Meta, qua OpenRouter)", ["sdwebui"] = "Stable Diffusion WebUI (chạy máy)",
        ["queued"] = "Đang chờ", ["running"] = "Đang chạy", ["succeeded"] = "Xong", ["failed"] = "Lỗi", ["cancelled"] = "Đã huỷ",
        ["waitingmanualretry"] = "Chờ bạn thử lại",
        ["shorts_select"] = "Chọn đoạn hay", ["shorts_render"] = "Cắt & dựng Shorts",
        ["voiceover"] = "Voiceover", ["music"] = "Nhạc nền", ["subtitle"] = "Phụ đề"
    };

    /// <summary>Nhãn tiếng Việt cho khoá/enum hiển thị trong danh sách chọn; không biết thì giữ nguyên.</summary>
    public static string Term(object? value)
    {
        var text = value?.ToString() ?? "";
        return Terms.TryGetValue(text, out var label) ? label : text;
    }
}

/// <summary>Dùng trong ItemTemplate của ComboBox: giá trị bind (khoá/enum) giữ nguyên, chỉ chữ hiển thị đổi sang tiếng Việt.</summary>
public sealed class LabelConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => Labels.Term(value);

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Hiện tên file thay cho đường dẫn đầy đủ trong danh sách hẹp; đường dẫn vẫn xem được qua tooltip.</summary>
public sealed class FileNameConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is string { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

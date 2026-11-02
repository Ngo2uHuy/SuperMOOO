using System.Reflection;
using Wpf.Ui.Controls;

namespace SuperMOOO.App.Shell;

public sealed record AppAssemblies(IReadOnlyList<Assembly> Items);

public sealed record ScreenDescriptor(string Id, string Title, string Category, int Order, Type? ViewType, string Description, SymbolRegular Icon)
{
    public string Display => $"{Id}  {Title}";
}

/// <summary>Danh sách cố định S01–S20; màn nào có class [Screen] thì thay placeholder, phase sau chỉ thêm class.</summary>
[Service]
public sealed class ScreenRegistry
{
    public const string Overview = "TỔNG QUAN";
    public const string Creative = "SÁNG TẠO NỘI DUNG";
    public const string Editing = "BIÊN TẬP & BIẾN ĐỔI VIDEO";
    public const string Workflow = "QUY TRÌNH & DỮ LIỆU";
    public const string Release = "PHÁT HÀNH & LIVESTREAM";

    private static readonly (string Id, string Title, string Category, string Description, SymbolRegular Icon)[] Planned =
    [
        ("S00", "Tổng quan", Overview, "Tình trạng tác vụ, sức khoẻ máy và việc cần xử lý.", SymbolRegular.Home24),
        ("S01", "Lồng tiếng video", Editing, "Nhận diện lời thoại, dịch và lồng tiếng lại video, kèm phụ đề và nhạc nền.", SymbolRegular.Translate24),
        ("S02", "Biên tập phụ đề", Editing, "Sửa thời gian, lời thoại và bản dịch từng câu rồi dùng lại cho lồng tiếng.", SymbolRegular.ClosedCaption24),
        ("S03", "Video YouTube & Talkshow", Creative, "Dựng video YouTube và talkshow từ kịch bản.", SymbolRegular.VideoPerson24),
        ("S04", "Review & tóm tắt phim", Editing, "Tóm tắt và review phim tự động.", SymbolRegular.MoviesAndTv24),
        ("S05", "Phòng thu giọng", Creative, "Đọc văn bản thành giọng nói, clone giọng và xử lý audio.", SymbolRegular.Mic24),
        ("S06", "Reup Studio", Editing, "Biến đổi video (khung hình, hiệu ứng, logo, giọng đọc, phụ đề) rồi xuất.", SymbolRegular.VideoClipMultiple24),
        ("S17", "Dựng video / Timeline", Editing, "Dựng clip, voiceover, nhạc và phụ đề; lưu dự án và xuất video.", SymbolRegular.VideoClipMultiple24),
        ("S07", "Auto A→Z", Workflow, "Chạy trọn quy trình từ nguồn đến video hoàn chỉnh.", SymbolRegular.Flowchart24),
        ("S08", "Reup tự động", Workflow, "Tự động tải, biến đổi và xuất video hàng loạt.", SymbolRegular.ArrowRepeatAll24),
        ("S09", "Tải video", Workflow, "Tải video từ liên kết hoặc file Excel/CSV và quản lý kho file đã tải.", SymbolRegular.ArrowDownload24),
        ("S10", "Crawler", Workflow, "Quét kênh, tìm kiếm, lấy bình luận.", SymbolRegular.GlobeSearch24),
        ("S11", "Theo dõi & Social Listening", Workflow, "Theo dõi kênh/từ khoá theo lịch, xem chỉ số và social listening.", SymbolRegular.DataTrending24),
        ("S12", "Thư viện tham khảo & bình luận", Workflow, "Tìm lại video đã lưu, quét bình luận theo chiến dịch, xem lịch sử tải.", SymbolRegular.Library24),
        ("S13", "Công cụ media", Editing, "Cắt, ghép, chuyển định dạng, tách âm thanh và các thao tác media khác.", SymbolRegular.Toolbox24),
        ("S14", "Đăng bài & lịch đăng", Release, "Kết nối tài khoản, soạn nội dung và lên lịch đăng.", SymbolRegular.Send24),
        ("S21", "Kho thành phẩm", Release, "Kho video đã làm xong để đăng: nhập từ pipeline, thư mục theo dõi, CSV/Excel; chống trùng và sửa thông tin.", SymbolRegular.VideoClip24),
        ("S22", "Chiến dịch đăng tự động", Release, "Chọn video từ kho, chia lượt giữa các kênh, đăng theo slot giờ; chỉ gợi ý, cần duyệt hoặc tự động; tự dừng khi có lỗi và tự đặt sản xuất khi hết video.", SymbolRegular.Megaphone24),
        ("S23", "Tài khoản & kênh", Release, "Kết nối tài khoản đăng bài, kênh/trang và tuyến mạng (proxy/VPN) riêng cho từng tài khoản.", SymbolRegular.PeopleCommunity24),
        ("S15", "Tác vụ (Jobs)", Release, "Theo dõi, huỷ và chạy lại các tác vụ nền.", SymbolRegular.TaskListLtr24),
        ("S18", "Video vẽ tay AI", Creative, "Tạo hàng loạt video whiteboard/stickman/hoạt hình từ chủ đề: AI viết kịch bản, vẽ ảnh, đọc lời và dựng video.", SymbolRegular.Pen24),
        ("S19", "Avatar khớp môi", Creative, "Biến ảnh chân dung và giọng đọc thành video người nói khớp môi.", SymbolRegular.PersonVoice24),
        ("S20", "Livestream bán hàng", Release, "Trợ lý livestream bán hàng: trả lời câu hỏi theo FAQ và phát hình/tiếng lên OBS.", SymbolRegular.Video24),
        ("S24", "Live tương tác", Release, "Chùa Online: tương tác TikFinity, cầu an và hiệu ứng trực tiếp.", SymbolRegular.Video24),
        ("S16", "Cài đặt & hệ thống", Release, "Đường dẫn, API key, model AI, mạng, giao diện và kiểm tra hệ thống.", SymbolRegular.Settings24)
    ];

    public ScreenRegistry(AppAssemblies assemblies)
    {
        var implemented = assemblies.Items
            .SelectMany(a => a.GetTypes())
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<ScreenAttribute>()))
            .Where(x => x.Attr is not null)
            .ToDictionary(x => x.Attr!.Id, x => (x.Type, Attr: x.Attr!));

        var unknown = implemented.Keys.Except(Planned.Select(p => p.Id)).FirstOrDefault();
        if (unknown is not null) throw new InvalidOperationException($"Màn {unknown} không nằm trong danh sách S01–S20.");

        All = Planned.Select((p, i) => implemented.TryGetValue(p.Id, out var impl)
                ? new ScreenDescriptor(p.Id, impl.Attr.Title, impl.Attr.Category, impl.Attr.Order, impl.Type, p.Description, p.Icon)
                : new ScreenDescriptor(p.Id, p.Title, p.Category, i + 1, null, p.Description, p.Icon))
            .OrderBy(d => d.Order)
            .ToList();
    }

    public IReadOnlyList<ScreenDescriptor> All { get; }

    public ScreenDescriptor? Find(string id) => All.FirstOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

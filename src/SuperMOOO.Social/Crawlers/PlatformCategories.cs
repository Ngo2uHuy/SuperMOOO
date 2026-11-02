namespace SuperMOOO.Social.Crawlers;

public sealed record CategoryOption(string Key, string Label);

/// <summary>Danh sách chuyên mục theo nền tảng (khoá lưu vào request; mỗi crawler tự ánh xạ khoá sang truy vấn thật). Nền tảng không có trong bảng = không có chuyên mục.</summary>
public static class PlatformCategories
{
    public static readonly CategoryOption All = new("all", "Tất cả");

    private static readonly IReadOnlyDictionary<string, CategoryOption[]> ByPlatform = new Dictionary<string, CategoryOption[]>
    {
        ["tiktok"] =
        [
            All, new("singing_dancing", "Ca nhạc & nhảy (Singing & Dancing)"), new("comedy", "Hài hước (Comedy)"), new("sports", "Thể thao (Sports)"),
            new("anime", "Anime & Truyện tranh (Anime & Comics)"), new("relationship", "Tình cảm (Relationship)"), new("shows", "Phim & Chương trình (Shows)"),
            new("lipsync", "Hát nhép (Lipsync)"), new("daily_life", "Đời sống (Daily Life)"), new("beauty", "Làm đẹp (Beauty Care)"),
            new("games", "Game (Games)"), new("society", "Xã hội (Society)"), new("outfit", "Thời trang (Outfit)"), new("cars", "Xe cộ (Cars)"),
            new("food", "Ẩm thực (Food)"), new("animals", "Động vật (Animals)"), new("tech", "Công nghệ (Tech)"), new("fitness", "Thể hình (Fitness)")
        ],
        ["douyin"] =
        [
            All, new("open_class", "Khoá học mở (公开课)"), new("game", "Game (游戏)"), new("acg", "Nhị thứ nguyên (二次元)"), new("music", "Âm nhạc (音乐)"),
            new("movie", "Phim ảnh (影视)"), new("food", "Ẩm thực (美食)"), new("knowledge", "Kiến thức (知识)"), new("drama", "Kịch ngắn (小剧场)"),
            new("vlog", "Vlog đời sống (生活vlog)"), new("sports", "Thể thao (体育)"), new("travel", "Du lịch (旅行)"), new("parenting", "Gia đình & trẻ em (亲子)"),
            new("animal", "Động vật (动物)"), new("rural", "Nông thôn (三农)"), new("car", "Ô tô (汽车)"), new("beauty_fashion", "Làm đẹp & thời trang (美妆穿搭)")
        ],
        ["bilibili"] =
        [
            All, new("anime", "Hoạt hình (动画)"), new("guochuang", "Quốc sáng (国创)"), new("music", "Âm nhạc (音乐)"), new("dance", "Nhảy (舞蹈)"),
            new("game", "Game (游戏)"), new("knowledge", "Kiến thức (知识)"), new("tech", "Công nghệ (科技)"), new("sports", "Thể thao (运动)"),
            new("car", "Ô tô (汽车)"), new("life", "Đời sống (生活)"), new("food", "Ẩm thực (美食)"), new("animal", "Động vật (动物圈)"),
            new("kichiku", "Quỷ súc (鬼畜)"), new("fashion", "Thời trang (时尚)"), new("entertainment", "Giải trí (娱乐)"), new("movie", "Điện ảnh (电影)"),
            new("tv", "Phim truyền hình (电视剧)")
        ],
        ["xiaohongshu"] =
        [
            All, new("fashion", "Thời trang (穿搭)"), new("food", "Ẩm thực (美食)"), new("makeup", "Trang điểm (彩妆)"), new("movie", "Phim ảnh (影视)"),
            new("career", "Công sở (职场)"), new("emotion", "Tình cảm (情感)"), new("home", "Nhà cửa (家居)"), new("game", "Game (游戏)"),
            new("travel", "Du lịch (旅行)"), new("fitness", "Thể hình (健身)"), new("lifestyle", "Đời sống (生活记录)"), new("beauty", "Người đẹp (美女)"),
            new("review", "Review đồ dùng (好物测评)"), new("photography", "Nhiếp ảnh (摄影)")
        ]
    };

    public static IReadOnlyList<CategoryOption> For(string? platform) =>
        platform is not null && ByPlatform.TryGetValue(platform.Trim().ToLowerInvariant(), out var list) ? list : [];

    /// <summary>Đoán nền tảng từ key operation (vd crawl.crawl_tiktok_explore_endpoint).</summary>
    public static string? PlatformOf(string operationKey) =>
        new[] { "tiktok", "douyin", "bilibili", "xiaohongshu", "weibo", "kuaishou", "youtube" }.FirstOrDefault(p => operationKey.Contains(p, StringComparison.OrdinalIgnoreCase));
}

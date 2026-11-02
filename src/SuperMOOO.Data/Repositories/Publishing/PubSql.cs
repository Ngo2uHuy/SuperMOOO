using System.Text.Json;
using System.Text.Json.Serialization;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.Data.Repositories.Publishing;

/// <summary>Tiện ích chung của kho đăng bài: cột *_at là Unix ms, enum lưu TEXT theo tên.</summary>
internal static class PubSql
{
    public const int SqliteConstraint = 19;

    // Cùng định dạng JSON với CenterPost (camelCase, enum dạng chuỗi) để dữ liệu chuyển sang vẫn đọc được.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static long ToMs(DateTimeOffset v) => v.ToUnixTimeMilliseconds();
    public static long? ToMs(DateTimeOffset? v) => v?.ToUnixTimeMilliseconds();
    public static DateTimeOffset FromMs(long v) => DateTimeOffset.FromUnixTimeMilliseconds(v);
    public static DateTimeOffset? FromMs(long? v) => v is { } n ? FromMs(n) : null;

    public static List<string> Names(IEnumerable<PublicationStatus> statuses) => statuses.Select(s => s.ToString()).ToList();

    public static List<string> StringList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json, Json) ?? []; }
        catch (JsonException) { return []; }
    }
}

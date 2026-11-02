namespace SuperMOOO.Workflows;

/// <summary>Đọc kết quả job pipeline: `{ "state": { ... }, "files": [ ... ] }`.</summary>
public static class PipelineResult
{
    public static string? Value(JsonNode? result, string key) =>
        result?["state"]?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    public static IReadOnlyList<string> Files(JsonNode? result) =>
        result?["files"] is JsonArray a ? [.. a.Select(f => f!.GetValue<string>())] : [];

    public static IReadOnlyList<string> Warnings(JsonNode? result) =>
        Value(result, "warnings")?.Split('\n', StringSplitOptions.RemoveEmptyEntries) ?? [];

    internal static readonly JsonSerializerOptions Json = Subtitles.SubtitleEntries.Json;

    internal static JsonNode? Node<T>(T value) => JsonSerializer.SerializeToNode(value, Json);

    /// <summary>Bật bước `deliver` của pipeline: video kết quả được xếp vào lịch đăng theo `delivery`.</summary>
    internal static void AttachDelivery(JsonObject input, Delivery.DeliveryOptions? delivery)
    {
        if (delivery is null) return;
        input["deliver"] = true;
        if (input["stepOptions"] is not JsonObject steps) input["stepOptions"] = steps = [];
        steps["deliver"] = Node(delivery);
    }

    internal static JsonObject Pipeline(string id, JsonObject input) => new() { ["pipeline"] = id, ["input"] = input };
}

using System.Globalization;

namespace SuperMOOO.Social.Crawlers;

public static class ContentFilters
{
    public static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new ArgumentException("Ngày phải có dạng yyyy-MM-dd (ví dụ 2026-10-28).");
    }

    public static void Validate(IReadOnlyDictionary<string, string>? filters)
    {
        var from = ParseDate(Get(filters, "date_from"));
        var to = ParseDate(Get(filters, "date_to"));
        if (from > to) throw new ArgumentException("Từ ngày không được sau Đến ngày.");
    }

    public static IReadOnlyList<ContentItem> Apply(IEnumerable<ContentItem> items, IReadOnlyDictionary<string, string>? filters)
    {
        Validate(filters);
        var from = ParseDate(Get(filters, "date_from"));
        var to = ParseDate(Get(filters, "date_to"));
        if (from is not null || to is not null)
            items = items.Where(i => i.PublishedAt is { } published
                && (from is null || DateOnly.FromDateTime(published.LocalDateTime) >= from)
                && (to is null || DateOnly.FromDateTime(published.LocalDateTime) <= to));
        items = Get(filters, "sort_by") switch
        {
            "latest" => items.OrderByDescending(i => i.PublishedAt),
            "oldest" => items.OrderBy(i => i.PublishedAt is null).ThenBy(i => i.PublishedAt),
            "popular" => items.OrderByDescending(i => i.ViewCount),
            _ => items
        };
        return items.ToList();
    }

    private static string? Get(IReadOnlyDictionary<string, string>? filters, string key) =>
        filters is not null && filters.TryGetValue(key, out var value) ? value : null;
}

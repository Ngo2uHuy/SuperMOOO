namespace SuperMOOO.Workflows.Creator;

public enum SlideOrder { Random, Alphabetical, Sequential }

/// <summary>Ảnh/clip nền cho video YouTube.</summary>
[Service]
public sealed class SlideLibrary
{
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".mp4", ".mov" };

    public IReadOnlyList<string> Scan(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Thư mục không tồn tại: {folder}");
        return Directory.EnumerateFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f)))
            .Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Slide của kịch bản thứ `index` (0-based): ngẫu nhiên, theo tên, hoặc lần lượt theo thứ tự đã sắp.</summary>
    public static string Pick(IReadOnlyList<string> slides, SlideOrder order, int index, Random? random = null)
    {
        if (slides.Count == 0) throw new ArgumentException("Chưa có slide nào.");
        return order switch
        {
            SlideOrder.Random => slides[(random ?? Random.Shared).Next(slides.Count)],
            SlideOrder.Alphabetical => slides.Order(StringComparer.OrdinalIgnoreCase).ElementAt(index % slides.Count),
            _ => slides[index % slides.Count]
        };
    }
}

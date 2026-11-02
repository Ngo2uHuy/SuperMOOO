namespace SuperMOOO.Media.Effects;

/// <summary>Tỉ lệ khung hình: một nơi duy nhất thay cho so khớp chuỗi rải rác.</summary>
public readonly record struct AspectRatio(int Width, int Height)
{
    public static readonly AspectRatio Original = new(0, 0);

    public bool IsOriginal => Width == 0;
    public double Value => IsOriginal ? 0 : (double)Width / Height;

    public static AspectRatio Parse(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || t.StartsWith("Original", StringComparison.OrdinalIgnoreCase) || t.Contains("gốc", StringComparison.OrdinalIgnoreCase))
            return Original;
        var parts = t.Split(' ')[0].Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w > 0 && h > 0)
            return new AspectRatio(w, h);
        throw new ArgumentException($"Tỉ lệ khung hình không hợp lệ: {text}", nameof(text));
    }

    /// <summary>Kích thước canvas (chẵn); shortSide là cạnh ngắn (1080 = 1080×1920 với 9:16). Original giữ kích thước nguồn.</summary>
    public (int Width, int Height) Canvas(int shortSide, (int Width, int Height) source)
    {
        if (IsOriginal) return (Ffmpeg.Fmt.Even(source.Width), Ffmpeg.Fmt.Even(source.Height));
        return Value >= 1
            ? (Ffmpeg.Fmt.Even(shortSide * Value), Ffmpeg.Fmt.Even(shortSide))
            : (Ffmpeg.Fmt.Even(shortSide), Ffmpeg.Fmt.Even(shortSide / Value));
    }
}

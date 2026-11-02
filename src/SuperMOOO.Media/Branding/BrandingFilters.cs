using SuperMOOO.Media.Effects;
using SuperMOOO.Media.Subtitles;
using SuperMOOO.Media.Whiteboard;

namespace SuperMOOO.Media.Branding;

/// <summary>Áp mẫu thương hiệu vào lệnh ffmpeg đang có: watermark chữ bằng ASS (libass) nối vào chuỗi -vf, logo ảnh overlay trong cùng lượt encode.</summary>
public static class BrandingFilters
{
    public const string WatermarkAssFile = "wm.ass";

    public static LogoOptions? Logo(BrandingPreset p) =>
        p.WatermarkEnabled && !string.IsNullOrWhiteSpace(p.LogoPath) && File.Exists(p.LogoPath)
            ? new LogoOptions(p.LogoPath, p.LogoPosition, p.LogoOpacity, p.LogoSizePercent, p.LogoX, p.LogoY)
            : null;

    /// <summary>Ghi wm.ass vào dir và trả filter `ass=...`; null khi không có chữ watermark. Chạy ffmpeg với WorkingDirectory = dir.</summary>
    public static async Task<string?> WriteWatermarkAsync(string dir, BrandingPreset p, int width, int height, CancellationToken ct)
    {
        var text = p.WatermarkText.Trim();
        if (!p.WatermarkEnabled || text.Length == 0) return null;
        Directory.CreateDirectory(dir);

        var inv = CultureInfo.InvariantCulture;
        var em = Math.Max(8, p.WatermarkSize * Math.Min(width, height) / 1080.0);
        var size = (int)Math.Round(em * TextMeasure.CellRatio(p.WatermarkFont));
        var margin = Math.Max(12, (int)Math.Round(Math.Min(width, height) * 0.025));
        var align = p.WatermarkPosition.ToUpperInvariant() switch { "TL" => 7, "BL" => 1, "BR" => 3, "C" or "CENTER" => 5, _ => 9 };
        var alpha = ((int)Math.Round((1 - Math.Clamp(p.WatermarkOpacity, 0, 1)) * 255)).ToString("X2", inv);
        // Toạ độ tự do: góc trên trái của hộp chữ (khớp hộp kéo thả ở S18); không có thì căn theo mã góc.
        var pos = p is { WatermarkX: { } fx, WatermarkY: { } fy }
            ? $"{{\\an7\\pos({(Math.Clamp(fx, 0, 100) * width / 100).ToString("0.#", inv)},{(Math.Clamp(fy, 0, 100) * height / 100).ToString("0.#", inv)})}}"
            : "";
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: ").Append(width).Append("\nPlayResY: ").Append(height)
          .Append("\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n[V4+ Styles]\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
          .Append("Style: Default,").Append(p.WatermarkFont.Replace(',', ' ')).Append(',').Append(size.ToString(inv)).Append(',')
          .Append(AssWriter.Color(p.WatermarkColor)).Append(',').Append(AssWriter.Color(p.WatermarkColor)).Append(',')
          .Append(AssWriter.Color(p.WatermarkOutline, 0, "#000000")).Append(",&H00000000,-1,0,0,0,100,100,0,0,1,")
          .Append(Math.Max(1, size / 14).ToString(inv)).Append(",0,").Append(align).Append(',').Append(margin).Append(',').Append(margin).Append(',').Append(margin)
          .Append(",1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n")
          .Append("Dialogue: 1,0:00:00.00,9:59:59.00,Default,,0,0,0,,{\\1a&H").Append(alpha).Append("&\\3a&H").Append(alpha).Append("&}")
          .Append(pos).Append(text.Replace("{", "(").Replace("}", ")").Replace("\\", "").Replace("\r", "").Replace("\n", " ")).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(dir, WatermarkAssFile), sb.ToString(), new UTF8Encoding(false), ct);
        return "ass=filename=" + WatermarkAssFile;
    }

    /// <summary>
    /// Thêm phần video của lệnh: không logo thì `-vf chain`; có logo thì thêm input logo (index logoInput) và `-filter_complex` ra [vout] + `-map [vout]`.
    /// Gọi sau khi đã thêm mọi `-i` khác và trước mọi `-map` khác (ffmpeg không cho option đầu ra đứng trước một `-i`).
    /// </summary>
    public static void AddVideoGraph(List<string> args, string chain, LogoOptions? logo, int logoInput, int width, int height)
    {
        if (logo is null)
        {
            if (chain.Length > 0) args.AddRange(["-vf", chain]);
            return;
        }

        args.AddRange(["-loop", "1", "-i", logo.Path]);
        var graph = new List<string> { $"[0:v]{(chain.Length > 0 ? chain : "null")}[v0]" };
        graph.AddRange(LogoOverlay.Build(width, height, logo, $"{logoInput}:v", "v0", "vout"));
        args.AddRange(["-filter_complex", string.Join(';', graph), "-map", "[vout]"]);
    }
}

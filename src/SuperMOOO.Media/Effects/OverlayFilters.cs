using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Effects;

/// <summary>Logo/watermark ảnh: dùng chung cho reup (ReupFilterGraphBuilder), công cụ Logo của S13 và LocalizeComposer.</summary>
public static class LogoOverlay
{
    /// <summary>Hai chuỗi filter: chuẩn bị logo [wm] từ input logoStream, rồi overlay lên inLabel → outLabel.</summary>
    public static string[] Build(int w, int h, LogoOptions logo, string logoStream, string inLabel, string outLabel)
    {
        var maxW = Fmt.Even(w * Math.Clamp(logo.MaxWidthPercent / 100, 0.05, 0.5));
        return
        [
            $"[{logoStream}]scale={maxW}:-1,format=rgba,colorchannelmixer=aa={Fmt.N(Math.Clamp(logo.Opacity, 0, 1), 3)}[wm]",
            $"[{inLabel}][wm]overlay={Position(w, h, logo)}:shortest=1,format=yuv420p[{outLabel}]"
        ];
    }

    /// <summary>Toạ độ tự do (% góc trên trái) kẹp trong khung; không có thì theo mã góc.</summary>
    public static string Position(int w, int h, LogoOptions logo) =>
        logo is { X: { } x, Y: { } y }
            ? $"W*{Fmt.N(Math.Clamp(x, 0, 100 - Math.Clamp(logo.MaxWidthPercent, 5, 50)), 3)}/100:H*{Fmt.N(Math.Clamp(y, 0, 100), 3)}/100"
            : Position(w, h, logo.Position);

    public static string Position(int w, int h, string position)
    {
        var m = Math.Max(12, (int)Math.Round(Math.Min(w, h) * 0.025));
        return position.ToUpperInvariant() switch
        {
            "TL" => $"{m}:{m}", "BL" => $"{m}:H-h-{m}", "BR" => $"W-w-{m}:H-h-{m}", "C" or "CENTER" => "(W-w)/2:(H-h)/2", _ => $"W-w-{m}:{m}"
        };
    }
}

public enum MaskStyle { Blur, Gradient, BlackBox, ColorBox }

/// <summary>Hộp pixel (x, y, w, h) trên khung đã biết kích thước.</summary>
public readonly record struct MaskBox(int X, int Y, int W, int H)
{
    /// <summary>Hộp theo phần trăm (0–100) của khung, kẹp trong khung (tối thiểu 2 px).</summary>
    public static MaskBox FromPercent(double x, double y, double w, double h, int width, int height)
    {
        var px = Math.Clamp((int)Math.Round(x * width / 100), 0, width - 2);
        var py = Math.Clamp((int)Math.Round(y * height / 100), 0, height - 2);
        var pw = Math.Clamp((int)Math.Round(w * width / 100), 2, width - px);
        var ph = Math.Clamp((int)Math.Round(h * height / 100), 2, height - py);
        return new MaskBox(px, py, pw, ph);
    }

    /// <summary>Vùng phụ đề cũ thường gặp (port compute_mask_box): bottom, bottom_thick, top, center.</summary>
    public static MaskBox Preset(string region, int width, int height) => region.ToLowerInvariant() switch
    {
        "bottom_thick" or "bottomthick" => new MaskBox(0, height - (int)(height * 0.22), width, (int)(height * 0.22)),
        "top" => new MaskBox(0, (int)(height * 0.02), width, (int)(height * 0.15)),
        "center" => new MaskBox(0, (height - (int)(height * 0.16)) / 2, width, (int)(height * 0.16)),
        _ => new MaskBox(0, Math.Max(0, height - (int)(height * 0.15) - (int)(height * 0.015)), width, (int)(height * 0.15))
    };
}

public static class MaskFilter
{
    /// <summary>
    /// Filter graph che một vùng từ inLabel ra outLabel. Blur: split → crop → boxblur → overlay (feather mờ viền, enable giới hạn theo mốc);
    /// các kiểu còn lại: drawbox. key tránh trùng nhãn khi dùng nhiều lần trong một graph.
    /// </summary>
    public static string Build(string inLabel, string outLabel, MaskBox box, MaskStyle style, string key = "m",
        int blurStrength = 18, int feather = 0, string? enable = null, string color = "black@0.88")
    {
        if (style != MaskStyle.Blur)
        {
            var fill = style == MaskStyle.ColorBox ? color : style == MaskStyle.Gradient ? "black@0.9" : "black@0.88";
            return $"[{inLabel}]drawbox=x={box.X}:y={box.Y}:w={box.W}:h={box.H}:color={fill}:t=fill[{outLabel}]";
        }

        var radius = Math.Max(1, Math.Min(blurStrength, Math.Min(box.W, box.H) / 2 - 1));
        var gate = enable is null ? "" : $":enable='{enable}'";
        if (feather <= 0)
            return $"[{inLabel}]split[base{key}][crop{key}];"
                   + $"[crop{key}]crop={box.W}:{box.H}:{box.X}:{box.Y},format=yuv444p,boxblur={radius}:2[blur{key}];"
                   + $"[base{key}][blur{key}]overlay={box.X}:{box.Y}{gate}[{outLabel}]";

        // Mặt nạ viền mờ không đổi theo khung: tính geq một lần trên một khung rồi alphamerge (repeatlast) thay vì geq 4 kênh mỗi khung — kết quả y hệt, nhanh hơn ~1/3.
        return $"[{inLabel}]split[base{key}][crop{key}];"
               + $"[crop{key}]crop={box.W}:{box.H}:{box.X}:{box.Y},format=yuv444p,boxblur={radius}:2,format=yuva444p[raw{key}];"
               + $"color=c=black:s={box.W}x{box.H}:r=1:d=1,format=gray,geq=lum='255*min(1,min(min(X,W-1-X),min(Y,H-1-Y))/{feather})'[alpha{key}];"
               + $"[raw{key}][alpha{key}]alphamerge[blur{key}];"
               + $"[base{key}][blur{key}]overlay={box.X}:{box.Y}{gate}[{outLabel}]";
    }
}

/// <summary>Hộp theo phần trăm (0–100) của khung; dùng chung cho vùng làm mờ/logo của S01 và S13.</summary>
public readonly record struct PercentBox(double X, double Y, double W, double H)
{
    public MaskBox ToPixels(int width, int height) => MaskBox.FromPercent(X, Y, W, H, width, height);
}

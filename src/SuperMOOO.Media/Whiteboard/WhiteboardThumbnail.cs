using System.Buffers;
using SuperMOOO.Media.Branding;
using SuperMOOO.Media.Ffmpeg;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>Thumbnail: ảnh cảnh phủ kín khung, gradient tối ở đáy, chữ in hoa viền đen (dòng đầu vàng). Chữ vẽ bằng libass (ASS).</summary>
[Service]
public sealed class WhiteboardThumbnail(FfmpegRunner ffmpeg)
{
    private const string Font = "Segoe UI Black";

    public async Task CreateAsync(WhiteboardThumbnailRequest request, CancellationToken ct)
    {
        var (w, h) = (request.Width, request.Height);
        if (w < 16 || h < 16) throw new ArgumentException("Kích thước thumbnail không hợp lệ.", nameof(request));
        var workDir = request.WorkingDirectory ?? Path.Combine(Path.GetTempPath(), "aivs_wb_" + Guid.NewGuid().ToString("N"));
        var ownDir = request.WorkingDirectory is null;
        var pixels = await RawImageDecoder.CoverAsync(ffmpeg, request.ImagePath, w, h, ct);
        try
        {
            Gradient(pixels.AsSpan(0, w * h * 4), w, h);
            var filters = new List<string>();
            var text = request.Text.Trim().ToUpper(CultureInfo.CurrentCulture);
            if (text.Length > 0)
            {
                Directory.CreateDirectory(workDir);
                await File.WriteAllTextAsync(Path.Combine(workDir, "thumb.ass"), BuildAss(text, w, h), new UTF8Encoding(false), ct);
                filters.Add("ass=filename=thumb.ass");
            }

            if (request.ExtraFilters is { Count: > 0 } extra) filters.AddRange(extra);
            var args = new List<string> { "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{w}x{h}", "-framerate", "1", "-i", "pipe:0" };
            BrandingFilters.AddVideoGraph(args, string.Join(',', filters), request.Logo, 1, w, h);
            args.AddRange(["-frames:v", "1"]);
            if (request.OutputPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || request.OutputPath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                args.AddRange(["-q:v", "2"]);

            var len = w * h * 4;
            await ffmpeg.RunAsync(new FfmpegJob(args, request.OutputPath, WorkingDirectory: workDir, Label: "Tạo thumbnail", Light: true,
                StdinWriter: (stdin, token) => stdin.WriteAsync(pixels.AsMemory(0, len), token).AsTask()), null, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
            if (ownDir) try { Directory.Delete(workDir, true); } catch (IOException) { /* thư mục tạm */ }
        }
    }

    // Tối dần từ 35% chiều cao xuống đáy, tới 0,85 đen.
    private static void Gradient(Span<byte> bgra, int w, int h)
    {
        var y0 = (int)(h * 0.35);
        var span = Math.Max(1, h - y0);
        for (var y = y0; y < h; y++)
        {
            var keep = 256 - (int)Math.Round(0.85 * 256 * (y - y0) / span);
            var row = bgra.Slice(y * w * 4, w * 4);
            for (var i = 0; i < row.Length; i += 4)
            {
                row[i] = (byte)(row[i] * keep >> 8);
                row[i + 1] = (byte)(row[i + 1] * keep >> 8);
                row[i + 2] = (byte)(row[i + 2] * keep >> 8);
            }
        }
    }

    private static string BuildAss(string text, int w, int h)
    {
        var lines = WhiteboardCues.WrapLines(text, Math.Max(14, Math.Min(18, (int)Math.Ceiling(text.Length / 3.0))));
        var maxW = w * 0.9;
        var maxH = h * 0.45;
        var px = (int)Math.Round(h * 0.16);
        for (; px > 12; px -= 2)
            if (lines.Max(l => TextMeasure.Measure(l, Font, px, false).Width) <= maxW && lines.Count * px * 1.1 <= maxH) break;

        var lh = px * 1.1;
        var outline = Math.Max(4, px * 0.16) / 2;
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("[Script Info]\nScriptType: v4.00+\nPlayResX: ").Append(w).Append("\nPlayResY: ").Append(h).Append("\nWrapStyle: 2\nScaledBorderAndShadow: yes\n\n")
          .Append("[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\n")
          .Append("Style: Default,").Append(Font).Append(',').Append(Math.Round(px * TextMeasure.CellRatio(Font)).ToString(inv)).Append(",&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,")
          .Append(outline.ToString("0.##", inv)).Append(",0,5,0,0,0,1\n\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n");
        var y = h - h * 0.05 - lines.Count * lh + lh / 2;
        for (var i = 0; i < lines.Count; i++)
        {
            var color = i == 0 ? "{\\c&H00E6FF&}" : "";
            sb.Append("Dialogue: 0,0:00:00.00,0:00:10.00,Default,,0,0,0,,{\\an5\\pos(").Append((w / 2.0).ToString("0.#", inv)).Append(',')
              .Append(y.ToString("0.#", inv)).Append(")}").Append(color).Append(lines[i].Replace("{", "(").Replace("}", ")").Replace("\\", "")).Append('\n');
            y += lh;
        }

        return sb.ToString();
    }
}

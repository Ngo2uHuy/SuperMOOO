namespace SuperMOOO.Media.Whiteboard;

/// <summary>Lớp nét vẽ: điểm ảnh tối hoặc có cạnh mạnh (Sobel). Pixel BGRA little-endian; 0 = không có nét.</summary>
public static class SketchMask
{
    /// <summary>lum, sketch, counts do người gọi cấp (có thể là buffer thuê, chưa xoá). counts: số điểm nét mỗi ô cs x cs.</summary>
    public static void Build(ReadOnlySpan<uint> color, int width, int height, int cs, int cols, Span<byte> lum, Span<uint> sketch, Span<ushort> counts)
    {
        var count = width * height;
        sketch[..count].Clear();
        counts[..(cols * ((height + cs - 1) / cs))].Clear();
        for (var i = 0; i < count; i++)
        {
            var p = color[i];
            lum[i] = (byte)((((p >> 16) & 0xFF) * 77 + ((p >> 8) & 0xFF) * 150 + (p & 0xFF) * 29) >> 8);
        }

        for (var y = 0; y < height; y++)
        {
            var cellRow = y / cs * cols;
            var interior = y > 0 && y < height - 1;
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                int l = lum[i];
                var on = l < 110;
                if (!on && interior && x > 0 && x < width - 1)
                {
                    int a = lum[i - width - 1], b = lum[i - width], c = lum[i - width + 1];
                    int d = lum[i - 1], f = lum[i + 1];
                    int g = lum[i + width - 1], h = lum[i + width], k = lum[i + width + 1];
                    var gx = c + 2 * f + k - a - 2 * d - g;
                    var gy = g + 2 * h + k - a - 2 * b - c;
                    on = Math.Abs(gx) + Math.Abs(gy) > 90;
                }

                if (!on) continue;
                var v = (uint)(l < 50 ? l : 50);
                sketch[i] = 0xFF000000u | (v << 16) | (v << 8) | v;
                counts[cellRow + x / cs]++;
            }
        }
    }
}

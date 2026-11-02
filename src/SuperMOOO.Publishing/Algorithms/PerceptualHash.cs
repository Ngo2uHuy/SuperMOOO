using System.Numerics;

namespace SuperMOOO.Publishing.Algorithms;

public static class PerceptualHash
{
    public const int Width = 9;
    public const int Height = 8;
    public const int FrameBytes = Width * Height;

    /// <summary>dHash 64-bit: mỗi hàng 9 điểm xám cho 8 bit so sánh điểm trái &gt; điểm phải.</summary>
    public static ulong DHash(ReadOnlySpan<byte> gray9x8)
    {
        if (gray9x8.Length != FrameBytes)
            throw new ArgumentException($"Cần đúng {FrameBytes} byte (ảnh xám {Width}x{Height}).", nameof(gray9x8));

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width - 1; x++, bit++)
                if (gray9x8[y * Width + x] > gray9x8[y * Width + x + 1]) hash |= 1UL << bit;
        return hash;
    }

    public static int Hamming(ulong a, ulong b) => BitOperations.PopCount(a ^ b);
}

/// <summary>Chữ ký video = dHash của K khung ở các tỉ lệ thời lượng cố định.</summary>
public sealed record VideoSignature(IReadOnlyList<ulong> FrameHashes)
{
    /// <summary>Tỉ lệ thời lượng lấy khung, tránh sát đầu/cuối (thường là logo/đen).</summary>
    public static IReadOnlyList<double> FrameRatios(int count) =>
        Enumerable.Range(1, count).Select(i => i / (double)(count + 1)).ToArray();

    public static VideoSignature FromFrames(IEnumerable<byte[]> grayFrames9x8) =>
        new(grayFrames9x8.Select(f => PerceptualHash.DHash(f)).ToArray());

    /// <summary>
    /// Độ tương đồng [0..1] = 1 - (trung bình Hamming / 64). Thử các độ lệch khung trong ±<paramref name="frameTolerance"/>
    /// để chịu được việc cắt đầu/cuối làm dịch các khung; lấy độ lệch khớp nhất.
    /// </summary>
    public static double Similarity(VideoSignature a, VideoSignature b, int frameTolerance = 1)
    {
        var ha = a.FrameHashes;
        var hb = b.FrameHashes;
        if (ha.Count == 0 || hb.Count == 0) return 0;

        var minOverlap = Math.Max(1, Math.Min(ha.Count, hb.Count) - frameTolerance);
        var best = double.MaxValue;
        for (var shift = -frameTolerance; shift <= frameTolerance; shift++)
        {
            var sum = 0;
            var n = 0;
            for (var i = 0; i < ha.Count; i++)
            {
                var j = i + shift;
                if (j < 0 || j >= hb.Count) continue;
                sum += PerceptualHash.Hamming(ha[i], hb[j]);
                n++;
            }
            if (n >= minOverlap) best = Math.Min(best, sum / (double)n);
        }
        return best == double.MaxValue ? 0 : 1 - best / 64.0;
    }
}

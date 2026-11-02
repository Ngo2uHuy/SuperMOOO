namespace SuperMOOO.Media.Whiteboard;

/// <summary>Thứ tự vẽ các ô có nét: đi tham lam theo 8 ô lân cận (nét liền), hết thì nhảy tới ô còn lại gần nhất bằng tìm theo vòng.</summary>
public static class StrokeOrder
{
    private static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (1, -1), (-1, -1)];

    private static void Test(ReadOnlySpan<byte> need, int cols, int rows, int x, int y, int cx, int cy, ref long bd, ref int bx, ref int by)
    {
        if (x < 0 || y < 0 || x >= cols || y >= rows || need[y * cols + x] == 0) return;
        long ddx = x - cx, ddy = y - cy;
        var d = ddx * ddx + ddy * ddy;
        if (d < bd) { bd = d; bx = x; by = y; }
    }

    /// <summary>need: buffer cols*rows byte (người gọi cấp, sẽ bị ghi đè); order: tối thiểu cols*rows phần tử. Trả về số ô đã xếp.</summary>
    public static int Build(ReadOnlySpan<ushort> counts, int cols, int rows, Span<byte> need, Span<int> order)
    {
        var cells = cols * rows;
        var remaining = 0;
        var first = -1;
        for (var i = 0; i < cells; i++)
        {
            if (counts[i] >= 1)
            {
                need[i] = 1;
                remaining++;
                if (first < 0) first = i;
            }
            else need[i] = 0;
        }

        if (first < 0) return 0;
        var n = 0;
        int cx = first % cols, cy = first / cols;
        need[first] = 0;
        order[n++] = first;
        remaining--;
        int pdx = 0, pdy = 0;
        var maxR = Math.Max(cols, rows);
        while (remaining > 0)
        {
            int bx = -1, by = -1;
            var bs = double.NegativeInfinity;
            foreach (var (dx, dy) in Dirs)
            {
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= cols || ny >= rows || need[ny * cols + nx] == 0) continue;
                var s = (dx * pdx + dy * pdy) / Math.Sqrt(dx * dx + dy * dy) - (dx != 0 && dy != 0 ? 0.15 : 0);
                if (s > bs) { bs = s; bx = nx; by = ny; }
            }

            if (bx < 0)
            {
                var bd = long.MaxValue;
                for (var r = 2; r <= maxR && bx < 0; r++)
                {
                    for (var d = -r; d <= r; d++) { Test(need, cols, rows, cx + d, cy - r, cx, cy, ref bd, ref bx, ref by); Test(need, cols, rows, cx + d, cy + r, cx, cy, ref bd, ref bx, ref by); }
                    for (var d = -r + 1; d < r; d++) { Test(need, cols, rows, cx - r, cy + d, cx, cy, ref bd, ref bx, ref by); Test(need, cols, rows, cx + r, cy + d, cx, cy, ref bd, ref bx, ref by); }
                }

                pdx = 0;
                pdy = 0;
            }
            else
            {
                pdx = bx - cx;
                pdy = by - cy;
            }

            cx = bx;
            cy = by;
            need[cy * cols + cx] = 0;
            order[n++] = cy * cols + cx;
            remaining--;
        }

        return n;
    }
}

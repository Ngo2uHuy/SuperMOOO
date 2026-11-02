using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace SuperMOOO.Media.Whiteboard;

/// <summary>Bàn tay đã nhân alpha (premultiplied), BGRA. Tip tính theo pixel sau khi scale.</summary>
public sealed class HandSprite(byte[] pixels, int width, int height, double tipX, double tipY)
{
    public byte[] Pixels { get; } = pixels;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double TipX { get; } = tipX;
    public double TipY { get; } = tipY;
}

/// <summary>
/// Dựng khung hình bằng buffer BGRA tái dùng (ArrayPool), không cấp phát mỗi khung. Giai đoạn vẽ giữ out = base ngoài vùng tay
/// (khôi phục chữ nhật tay cũ + chép các ô mới) nên không phải sao chép cả khung; giai đoạn chuyển màu mới nội suy toàn khung bằng Vector256.
/// </summary>
public sealed class FrameComposer : IDisposable
{
    private readonly int _w, _h, _cs, _cols, _n;
    private readonly double _k, _drawSec, _fade, _fadeEnd;
    private readonly HandSprite? _hand;
    private readonly byte[] _color, _base, _out, _sketch;
    private readonly int[] _order;
    private readonly int _pixelBytes;

    private int _shown;
    private double _tx, _ty, _px, _py, _fromX, _fromY;
    private int _handX, _handY, _handW, _handH;     // chữ nhật tay khung trước trong out (0 rộng = không có)

    /// <summary>Số khung có chuyển động (t &lt; fadeEnd); khung kế tiếp là khung cuối đứng yên.</summary>
    public int MotionFrames { get; }
    public int Width => _w;
    public int Height => _h;

    public FrameComposer(ReadOnlySpan<byte> colorBgra, int width, int height, int fps, double audioDurationSec, WhiteboardRenderOptions options, HandSprite? hand)
    {
        _w = width;
        _h = height;
        _hand = hand;
        _pixelBytes = width * height * 4;
        var pool = ArrayPool<byte>.Shared;
        _color = pool.Rent(_pixelBytes);
        _base = pool.Rent(_pixelBytes);
        _out = pool.Rent(_pixelBytes);
        _sketch = pool.Rent(_pixelBytes);
        var lum = pool.Rent(width * height);
        try
        {
            colorBgra[.._pixelBytes].CopyTo(_color);
            var short_ = Math.Min(width, height);
            _k = short_ / 1080.0;
            _cs = Math.Max(4, (int)Math.Round(8.0 * short_ / 1080, MidpointRounding.AwayFromZero));
            _cols = (width + _cs - 1) / _cs;
            var rows = (height + _cs - 1) / _cs;

            var counts = ArrayPool<ushort>.Shared.Rent(_cols * rows);
            var need = pool.Rent(_cols * rows);
            _order = ArrayPool<int>.Shared.Rent(_cols * rows);
            try
            {
                SketchMask.Build(MemoryMarshal.Cast<byte, uint>(_color.AsSpan(0, _pixelBytes)), width, height, _cs, _cols, lum,
                    MemoryMarshal.Cast<byte, uint>(_sketch.AsSpan(0, _pixelBytes)), counts);
                _n = StrokeOrder.Build(counts, _cols, rows, need, _order);
            }
            finally
            {
                ArrayPool<ushort>.Shared.Return(counts);
                pool.Return(need);
            }
        }
        finally { pool.Return(lum); }

        FillWhite(_base.AsSpan(0, _pixelBytes));
        FillWhite(_out.AsSpan(0, _pixelBytes));

        var tail = Math.Max(0, options.TailHoldSec);
        var total = audioDurationSec + tail;
        _fade = Math.Max(0, options.ColorFadeSec);
        var drawSec = Math.Clamp(audioDurationSec * 0.6, 1.2, 10) / Math.Max(0.1, options.DrawSpeed);
        drawSec = Math.Max(0.5, Math.Min(drawSec, total - _fade - 0.2));
        // audio rất ngắn: thu ngắn fade để những khung cuối đã đủ màu
        _fade = Math.Min(_fade, Math.Max(0, total - drawSec - 0.1));
        _drawSec = drawSec;
        _fadeEnd = drawSec + _fade;
        MotionFrames = Math.Min((int)Math.Ceiling(total * fps), (int)Math.Ceiling(_fadeEnd * fps));
        TotalFrames = (int)Math.Ceiling(total * fps);

        _tx = width * 0.95;
        _ty = height + (hand is null ? 0 : hand.TipY * 0.3);
        _px = _tx;
        _py = _ty;
        _fromX = _px;
        _fromY = _py;
    }

    public int TotalFrames { get; }

    /// <summary>Khung f (0..MotionFrames-1) dạng BGRA; nội dung hợp lệ đến khi gọi Compose/Final kế tiếp.</summary>
    public ReadOnlyMemory<byte> Compose(int f, int fps)
    {
        var t = (double)f / fps;
        var bas = _base.AsSpan(0, _pixelBytes);
        var o = _out.AsSpan(0, _pixelBytes);
        if (t < _drawSec)
        {
            RestoreHandRect(bas, o);
            var p = Math.Clamp(t / _drawSec, 0, 1);
            var target = (int)Math.Floor((0.5 * p + 0.5 * Smooth(p)) * _n + 0.5);
            for (; _shown < target; _shown++) DrawCell(_order[_shown], bas, o);
            if (_hand is not null)
            {
                _px += (_tx - _px) * 0.45;
                _py += (_ty - _py) * 0.45;
                var hx = _px + Math.Sin(f * 0.9) * 3 * _k;
                var hy = _py + Math.Cos(f * 1.3) * 3 * _k;
                _fromX = hx;
                _fromY = hy;
                DrawHand(o, hx, hy, remember: true);
            }
        }
        else
        {
            for (; _shown < _n; _shown++) DrawCell(_order[_shown], bas, o);
            _handW = 0;
            var a = _fade > 0 ? Math.Clamp((t - _drawSec) / _fade, 0, 1) : 1;
            if (a >= 1) _color.AsSpan(0, _pixelBytes).CopyTo(o);
            else Lerp(bas, _color.AsSpan(0, _pixelBytes), o, (int)Math.Round(a * 256));
            if (_hand is not null)
            {
                var e = Smooth(_fade > 0 ? Math.Clamp((t - _drawSec) / _fade, 0, 1) : 1);
                var exitX = _w + _hand.TipX + 20;
                var exitY = _h + _hand.TipY + 20;
                DrawHand(o, _fromX + (exitX - _fromX) * e, _fromY + (exitY - _fromY) * e, remember: false);
            }
        }

        return _out.AsMemory(0, _pixelBytes);
    }

    /// <summary>Khung cuối đứng yên: ảnh màu đầy đủ, không tay.</summary>
    public ReadOnlyMemory<byte> Final() => _color.AsMemory(0, _pixelBytes);

    private static double Smooth(double p) => p * p * (3 - 2 * p);

    private static void FillWhite(Span<byte> buffer) => buffer.Fill(0xFF);

    private void RestoreHandRect(ReadOnlySpan<byte> bas, Span<byte> o)
    {
        if (_handW <= 0) return;
        for (var y = _handY; y < _handY + _handH; y++)
        {
            var off = (y * _w + _handX) * 4;
            bas.Slice(off, _handW * 4).CopyTo(o.Slice(off));
        }

        _handW = 0;
    }

    // Cọ tròn (r > nửa đường chéo ô) che dấu vuông của lưới. Mép cọ khử răng cưa như clip arc của canvas gốc: điểm nét ở rìa
    // đường tròn trộn theo độ phủ (source-over) lên nền, bên trong giữ chép thẳng.
    private void DrawCell(int c, Span<byte> bas, Span<byte> o)
    {
        var x = c % _cols * _cs;
        var y = c / _cols * _cs;
        double cx = x + _cs / 2.0, cy = y + _cs / 2.0;
        var r = _cs * 1.05;
        var inner = (r - 0.5) * (r - 0.5);
        var outer = (r + 0.5) * (r + 0.5);
        int bx = Math.Max(0, (int)Math.Floor(cx - r - 1)), by = Math.Max(0, (int)Math.Floor(cy - r - 1));
        int ex = Math.Min(_w, (int)Math.Ceiling(cx + r + 1)), ey = Math.Min(_h, (int)Math.Ceiling(cy + r + 1));
        var sk = MemoryMarshal.Cast<byte, uint>(_sketch.AsSpan(0, _pixelBytes));
        var b32 = MemoryMarshal.Cast<byte, uint>(bas);
        var o32 = MemoryMarshal.Cast<byte, uint>(o);
        for (var yy = by; yy < ey; yy++)
        {
            var dy = yy + 0.5 - cy;
            var row = yy * _w;
            for (var xx = bx; xx < ex; xx++)
            {
                var s = sk[row + xx];
                if (s == 0) continue;
                var dx = xx + 0.5 - cx;
                var d2 = dx * dx + dy * dy;
                if (d2 >= outer) continue;
                if (d2 <= inner)
                {
                    b32[row + xx] = s;
                    o32[row + xx] = s;
                    continue;
                }

                var a = (uint)Math.Clamp((r + 0.5 - Math.Sqrt(d2)) * 255, 0, 255);
                var d = b32[row + xx];
                var ia = 255 - a;
                var blended = 0xFF000000u
                    | ((((s >> 16) & 0xFF) * a + ((d >> 16) & 0xFF) * ia + 127) / 255 << 16)
                    | ((((s >> 8) & 0xFF) * a + ((d >> 8) & 0xFF) * ia + 127) / 255 << 8)
                    | ((s & 0xFF) * a + (d & 0xFF) * ia + 127) / 255;
                b32[row + xx] = blended;
                o32[row + xx] = blended;
            }
        }

        _tx = cx;
        _ty = cy;
    }

    private void DrawHand(Span<byte> o, double hx, double hy, bool remember)
    {
        var hand = _hand!;
        var x0 = (int)Math.Floor(hx - hand.TipX + 0.5);
        var y0 = (int)Math.Floor(hy - hand.TipY + 0.5);
        int sx = Math.Max(0, -x0), sy = Math.Max(0, -y0);
        var ex = Math.Min(hand.Width, _w - x0);
        var ey = Math.Min(hand.Height, _h - y0);
        if (ex <= sx || ey <= sy) return;

        var src = MemoryMarshal.Cast<byte, uint>(hand.Pixels.AsSpan(0, hand.Width * hand.Height * 4));
        var dst = MemoryMarshal.Cast<byte, uint>(o);
        for (var y = sy; y < ey; y++)
        {
            var srow = y * hand.Width;
            var drow = (y0 + y) * _w + x0;
            for (var x = sx; x < ex; x++)
            {
                var s = src[srow + x];
                var a = s >> 24;
                if (a == 0) continue;
                if (a == 255) { dst[drow + x] = s; continue; }
                var d = dst[drow + x];
                var inv = 255 - a;
                // premultiplied: out = s + d * (1 - a); x / 255 ~ (x * 0x8081) >> 23 đúng với x < 65535
                var b = (s & 0xFF) + (((d & 0xFF) * inv * 0x8081u) >> 23);
                var g = ((s >> 8) & 0xFF) + ((((d >> 8) & 0xFF) * inv * 0x8081u) >> 23);
                var rr = ((s >> 16) & 0xFF) + ((((d >> 16) & 0xFF) * inv * 0x8081u) >> 23);
                dst[drow + x] = 0xFF000000u | (Math.Min(rr, 255u) << 16) | (Math.Min(g, 255u) << 8) | Math.Min(b, 255u);
            }
        }

        if (remember)
        {
            _handX = x0 + sx;
            _handY = y0 + sy;
            _handW = ex - sx;
            _handH = ey - sy;
        }
    }

    /// <summary>dst = (a * (256 - w) + b * w) >> 8 cho từng byte; w 0..256. Cả hai buffer đều opaque nên alpha giữ 255.</summary>
    private static void Lerp(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst, int w)
    {
        var i = 0;
        if (Vector256.IsHardwareAccelerated && a.Length >= Vector256<byte>.Count)
        {
            var wv = Vector256.Create((ushort)w);
            var iv = Vector256.Create((ushort)(256 - w));
            var last = a.Length - Vector256<byte>.Count;
            for (; i <= last; i += Vector256<byte>.Count)
            {
                var (aLo, aHi) = Vector256.Widen(Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(a), (nuint)i));
                var (bLo, bHi) = Vector256.Widen(Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(b), (nuint)i));
                var lo = Vector256.ShiftRightLogical(aLo * iv + bLo * wv, 8);
                var hi = Vector256.ShiftRightLogical(aHi * iv + bHi * wv, 8);
                Vector256.Narrow(lo, hi).StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)i);
            }
        }

        for (; i < a.Length; i++) dst[i] = (byte)((a[i] * (256 - w) + b[i] * w) >> 8);
    }

    public void Dispose()
    {
        var pool = ArrayPool<byte>.Shared;
        pool.Return(_color);
        pool.Return(_base);
        pool.Return(_out);
        pool.Return(_sketch);
        ArrayPool<int>.Shared.Return(_order);
    }
}

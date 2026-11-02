namespace SuperMOOO.Publishing.Connectors;

/// <summary>
/// Giới hạn băng thông upload dùng chung cho mọi upload đang chạy (token bucket dạng "đặt chỗ thời gian"). Đổi được khi Worker đang chạy;
/// 0 = không giới hạn.
/// </summary>
public static class UploadThrottle
{
    private static readonly object Gate = new();
    private static long _bytesPerSecond;
    private static long _nextFreeTicks;

    public static void SetLimitKbps(int kbps)
    {
        lock (Gate)
        {
            _bytesPerSecond = kbps > 0 ? kbps * 1024L : 0;
            _nextFreeTicks = 0;
        }
    }

    /// <summary>Chờ tới lượt gửi <paramref name="bytes"/>; mỗi lần gọi đặt chỗ phần thời gian tương ứng nên nhiều upload chia sẻ chung hạn mức.</summary>
    public static async Task WaitAsync(int bytes, CancellationToken ct)
    {
        TimeSpan delay;
        lock (Gate)
        {
            if (_bytesPerSecond == 0) return;
            var now = TimeProvider.System.GetTimestamp();
            var nowTicks = (long)(now * (TimeSpan.TicksPerSecond / (double)TimeProvider.System.TimestampFrequency));
            var start = Math.Max(nowTicks, _nextFreeTicks);
            _nextFreeTicks = start + (long)(bytes * (double)TimeSpan.TicksPerSecond / _bytesPerSecond);
            delay = TimeSpan.FromTicks(start - nowTicks);
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}

/// <summary>Stream ghi bọc quanh stream đích, nhường nhịp theo <see cref="UploadThrottle"/> trước mỗi lần ghi.</summary>
public sealed class ThrottledStream(Stream inner) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        await UploadThrottle.WaitAsync(buffer.Length, ct);
        await inner.WriteAsync(buffer, ct);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

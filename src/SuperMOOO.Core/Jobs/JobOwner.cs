namespace SuperMOOO.Core.Jobs;

/// <summary>
/// Chủ sở hữu job theo ngữ cảnh gọi: màn bọc lệnh xếp job trong <see cref="Begin"/> để mọi service bên dưới gắn owner
/// mà không phải đổi chữ ký; IJobQueue chỉ dùng khi request chưa tự đặt Owner.
/// </summary>
public static class JobOwner
{
    private static readonly AsyncLocal<string?> Ambient = new();

    public static string? Current => Ambient.Value;

    /// <summary>Owner của job do một màn tạo, ví dụ "screen:S01".</summary>
    public static string Screen(string screenId) => "screen:" + screenId;

    public static IDisposable Begin(string owner)
    {
        var previous = Ambient.Value;
        Ambient.Value = owner;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

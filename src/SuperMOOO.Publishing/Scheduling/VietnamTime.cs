namespace SuperMOOO.Publishing.Scheduling;

/// <summary>Giờ Việt Nam (UTC+7, không DST): lịch đăng nhập theo giờ VN, lưu UTC.</summary>
public static class VietnamTime
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(7);

    public static DateTimeOffset FromLocal(DateTime vnLocal) => new(DateTime.SpecifyKind(vnLocal, DateTimeKind.Unspecified), Offset);

    public static DateTime ToLocal(DateTimeOffset value) => value.ToOffset(Offset).DateTime;
}

using System.Diagnostics;

namespace SuperMOOO.Core.Abstractions;

public interface IRuntimeResources
{
    MachineResources Machine { get; }
    PerformancePlan Plan { get; }
    ResourceLoad Load { get; }
    /// <summary>Đổi mức tải hoặc mức ưu tiên tiến trình con.</summary>
    event EventHandler<ResourceLoad>? LoadChanged;
    /// <summary>Lặp mỗi chu kỳ đo khi RAM trống dưới mức dự phòng (chỉ chế độ tự động): giải phóng server AI đang rảnh.</summary>
    event EventHandler? MemoryCritical;
    Task<IDisposable> AcquireMediaAsync(CancellationToken ct);
    /// <summary>Số luồng cho tiến trình media sắp khởi động, đã giảm khi máy đang bận.</summary>
    int CurrentMediaThreads();
    /// <summary>Theo dõi tiến trình con nặng do app khởi động (đo CPU, hạ ưu tiên khi máy bận). Dispose khi tiến trình kết thúc.</summary>
    IDisposable TrackProcess(Process process);
}

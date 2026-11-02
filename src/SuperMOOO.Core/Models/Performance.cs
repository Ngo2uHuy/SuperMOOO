namespace SuperMOOO.Core.Models;

// Section "performance". Auto is computed locally each launch; never persisted as machine-specific limits.
public sealed class PerformanceSettings
{
    public bool Automatic { get; set; } = true;
    public int CpuBudgetPercent { get; set; } = 75;
    public int MediaParallel { get; set; } = 1;
    public int MediaThreads { get; set; } = 4;
    public int AiThreads { get; set; } = 2;
    public int ReservedMemoryGb { get; set; } = 4;
    public string Encoder { get; set; } = "auto";
}

// PhysicalCores = 0: không đọc được, ước lượng bằng nửa số luồng (SMT).
public sealed record MachineResources(string CpuName, int LogicalProcessors, long TotalMemoryBytes, long AvailableMemoryBytes, int PhysicalCores = 0);
public sealed record PerformancePlan(int MediaParallel, int MediaThreads, int AiThreads, int NetworkParallel,
    int ReservedMemoryGb, string Encoder, bool Automatic);

public enum ResourcePressure { Normal, High, Critical }
/// <summary>Tải máy đã làm mượt. OtherCpuPercent: phần CPU của ứng dụng khác (tổng trừ app và tiến trình con nặng).</summary>
public sealed record ResourceLoad(double CpuPercent, double OtherCpuPercent, long AvailableMemoryBytes, ResourcePressure Pressure, bool ChildrenBelowNormal);

public static class PerformancePlanner
{
    public static PerformancePlan Calculate(MachineResources machine, PerformanceSettings settings)
    {
        var cpus = Math.Max(1, machine.LogicalProcessors);
        var gb = Math.Max(1, machine.TotalMemoryBytes / 1073741824d);
        var budget = Math.Max(1, (int)Math.Floor(cpus * Math.Clamp(settings.CpuBudgetPercent, 25, 90) / 100d));
        if (settings.Automatic)
        {
            var parallel = cpus >= 16 && gb >= 31 ? 2 : 1;
            // ggml/ctranslate2 chạy nhanh nhất ở số nhân vật lý; luồng SMT thêm vào làm chậm đi.
            var cores = machine.PhysicalCores > 0 ? machine.PhysicalCores : Math.Max(1, cpus / 2);
            var ai = Math.Clamp(Math.Min(cores, budget), 1, 16);
            // Media và AI hiếm khi chạy cùng lúc trong pipeline nên không trừ phần AI; vẫn chừa 1/3 ngân sách cho thao tác foreground.
            var media = Math.Clamp((budget - budget / 3) / parallel, 1, 12);
            return new PerformancePlan(parallel, media, ai, gb < 8 ? 2 : 3,
                Math.Max(2, (int)Math.Ceiling(gb * 0.15)), "auto", true);
        }
        return new PerformancePlan(Math.Clamp(settings.MediaParallel, 1, Math.Min(4, cpus)),
            Math.Clamp(settings.MediaThreads, 1, cpus), Math.Clamp(settings.AiThreads, 1, cpus), 3,
            Math.Clamp(settings.ReservedMemoryGb, 1, Math.Max(1, (int)gb - 1)),
            settings.Encoder is "auto" or "x264" or "qsv" or "nvenc" ? settings.Encoder : "auto", false);
    }
}

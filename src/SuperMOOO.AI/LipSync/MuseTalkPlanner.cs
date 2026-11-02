using System.Text.Json;
using SuperMOOO.Core;

namespace SuperMOOO.AI.LipSync;

public sealed record MuseTalkPlan(int Threads, int BatchSize, int MaxSide, int Fps, string X264Preset, int EtaSeconds, int TimeoutSeconds, IReadOnlyList<string> Notes);

/// <summary>
/// Chọn luồng/batch theo máy và ước tính thời gian từ tốc độ đo được (trung bình trượt mũ lưu ở lipsync\perf.json).
/// Số liệu mặc định là giây xử lý trên mỗi giây audio (CPU ~40) và mỗi giây avatar khi dò mặt (~70); lần dựng thật sẽ hiệu chỉnh.
/// </summary>
[Service]
public sealed class MuseTalkPlanner(AppPaths paths, IRuntimeResources? resources = null)
{
    public const int MaxSide = 1280;
    public const int Fps = 25;
    private const double DefaultInference = 40.0;
    private const double DefaultLandmarks = 70.0;
    private static readonly object FileLock = new();

    public MuseTalkPlan Plan(double audioSeconds, double detectSeconds)
    {
        var machine = resources?.Machine;
        var cores = machine is null ? Math.Max(1, Environment.ProcessorCount / 2)
            : machine.PhysicalCores > 0 ? machine.PhysicalCores : Math.Max(1, machine.LogicalProcessors / 2);
        // Chừa một nhân cho app, OBS và FFmpeg.
        var threads = Math.Max(1, Math.Min(16, cores - 1));
        var availableBytes = resources?.Load.AvailableMemoryBytes is > 0 ? resources.Load.AvailableMemoryBytes : machine?.AvailableMemoryBytes ?? 0;
        var availableGb = availableBytes > 0 ? availableBytes / 1073741824.0 : 4.0;
        // CPU giữ ~4 GB model; mỗi khung thêm trong batch cần dư RAM.
        var batch = availableGb < 6 ? 1 : availableGb < 10 ? 2 : availableGb < 16 ? 4 : 8;
        var weak = cores <= 4 || availableGb < 6;

        var rates = ReadRates();
        var eta = rates.GetValueOrDefault("cpu", DefaultInference) * audioSeconds + rates.GetValueOrDefault("cpu-landmarks", DefaultLandmarks) * detectSeconds;
        var notes = new List<string>();
        if (availableGb < 4) notes.Add($"RAM trống chỉ còn {availableGb:0.0} GB; đóng bớt ứng dụng trước khi dựng khớp môi.");
        return new MuseTalkPlan(threads, batch, MaxSide, Fps, weak ? "veryfast" : "fast", (int)Math.Round(eta),
            Math.Max(600, (int)Math.Round(3 * eta) + 300), notes);
    }

    /// <summary>Hiệu chỉnh tốc độ: phần thời gian dò mặt tách riêng (nếu có) để lần sau đã cache vẫn ước tính đúng.</summary>
    public void RecordRate(double audioSeconds, double elapsedSeconds, double detectSeconds)
    {
        if (audioSeconds <= 0) return;
        lock (FileLock)
        {
            var rates = ReadRates();
            string key;
            double measured;
            if (detectSeconds > 0)
            {
                key = "cpu-landmarks";
                measured = Math.Max(0, elapsedSeconds - rates.GetValueOrDefault("cpu", DefaultInference) * audioSeconds) / detectSeconds;
            }
            else
            {
                key = "cpu";
                measured = elapsedSeconds / audioSeconds;
            }

            rates[key] = Math.Round(rates.TryGetValue(key, out var previous) ? 0.3 * measured + 0.7 * previous : measured, 3);
            var file = MuseTalkLocation.PerfFile(paths);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(rates));
            File.Move(temp, file, overwrite: true);
        }
    }

    private Dictionary<string, double> ReadRates()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(MuseTalkLocation.PerfFile(paths))) ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }
}

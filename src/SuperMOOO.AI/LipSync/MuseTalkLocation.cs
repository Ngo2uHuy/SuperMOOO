using SuperMOOO.Core;

namespace SuperMOOO.AI.LipSync;

/// <summary>Vị trí runtime MuseTalk. Venv Python 3.10 riêng (AI worker cần Python ≥ 3.14 nên không dùng chung); weights nằm cùng thư mục model của app.</summary>
public static class MuseTalkLocation
{
    public static string Root(AppPaths p) => Path.Combine(p.DataRoot, "lipsync", "musetalk");
    public static string Venv(AppPaths p) => Path.Combine(Root(p), "venv");
    public static string Python(AppPaths p) => Path.Combine(Venv(p), "Scripts", "python.exe");
    public static string Source(AppPaths p) => Path.Combine(Root(p), "src");
    public static string Weights(AppPaths p) => Path.Combine(p.Models, "musetalk");

    /// <summary>Thư viện avatar nằm ngoài cache\ để CacheJanitor không xoá toạ độ mặt đã dò.</summary>
    public static string Avatars(AppPaths p) => Path.Combine(p.DataRoot, "lipsync", "avatars");
    public static string PerfFile(AppPaths p) => Path.Combine(p.DataRoot, "lipsync", "perf.json");

    public const string InferenceScript = "scripts/inference.py";

    public static readonly IReadOnlyList<string> RequiredWeights =
    [
        "musetalkV15/unet.pth", "musetalkV15/musetalk.json", "sd-vae/config.json", "whisper/config.json",
        "dwpose/dw-ll_ucoco_384.pth", "face-parse-bisent/79999_iter.pth", "face-parse-bisent/resnet18-5c106cde.pth"
    ];

    public static IReadOnlyList<string> MissingWeights(AppPaths p) =>
        [.. RequiredWeights.Where(f => !File.Exists(Path.Combine(Weights(p), f.Replace('/', Path.DirectorySeparatorChar))))];

    public static bool HasSource(AppPaths p) => File.Exists(Path.Combine(Source(p), InferenceScript.Replace('/', Path.DirectorySeparatorChar)));
}

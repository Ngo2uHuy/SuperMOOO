using System.Globalization;
using SuperMOOO.AI.Worker;
using SuperMOOO.AI.Worker.V1;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.AI.Tts;

/// <summary>
/// Tạo codes + embedding VieNeu từ một mẫu giọng (clone tiếng Việt, giọng neo của giọng dựng sẵn) để VieNeu đọc theo giọng đó.
/// audio.cpp không tự tính embedding (thiếu nó VieNeu đọc sai lời): embedding 192 chiều do AI worker tính bằng speaker encoder của VieNeu,
/// codes do audiocpp_cli mã hoá từ 8 s đầu như SDK VieNeu.
/// </summary>
[Service]
public sealed class VieNeuEnroller(IProcessRunner runner, IToolLocator tools, AppPaths paths, NativeTtsServerManager server,
    IAiWorkerClient worker, ILogger<VieNeuEnroller> log)
{
    /// <summary>Codes và embedding đi kèm mẫu (cùng thư mục, cùng tên); có đủ cả hai thì VieNeu đọc được giọng của mẫu.</summary>
    public static (string Codes, string Embedding) Files(string sample) =>
        (Path.ChangeExtension(sample, ".vieneu_codes.txt"), Path.ChangeExtension(sample, ".vieneu_emb.txt"));

    private int _warned;

    /// <summary>Đủ VieNeu, model speaker và Python của AI worker; thiếu thì ghi cảnh báo (một lần) để nơi gọi dùng OmniVoice.</summary>
    public bool IsReady()
    {
        if (server.IsInstalled("vieneu") && File.Exists(WorkerLocation.SpeakerModel(paths)) && tools.TryResolve(ToolKind.Python, out _)) return true;
        if (Interlocked.Exchange(ref _warned, 1) == 0) log.LogWarning("Giọng tiếng Việt theo mẫu đọc bằng OmniVoice: thiếu VieNeu, AI worker hoặc model speaker (S16 → Tải model).");
        return false;
    }

    public async Task EnrollAsync(string sample, CancellationToken ct)
    {
        var (codes, embeddingFile) = Files(sample);
        if (!tools.TryResolve(ToolKind.NativeTtsServer, out var exe)) throw new InvalidOperationException("Thiếu audiocpp native_tts.");
        var trimmed = Path.ChangeExtension(sample, ".vieneu.tmp.wav");
        var outDir = Path.ChangeExtension(sample, ".vieneu.tmp");
        try
        {
            var cut = await runner.RunAsync(new ProcessSpec(tools.Resolve(ToolKind.Ffmpeg),
                ["-y", "-hide_banner", "-loglevel", "error", "-i", sample, "-t", "8", "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", trimmed]), null, ct);
            if (cut.ExitCode != 0) throw new InvalidOperationException("ffmpeg không cắt được mẫu giọng.");

            var response = await worker.SpeakerEmbeddingAsync(new SpeakerEmbeddingRequest { InputPath = trimmed, ModelPath = WorkerLocation.SpeakerModel(paths) }, ct);
            if (response.Embedding.Count != 192) throw new InvalidOperationException($"Embedding có {response.Embedding.Count} chiều, cần 192.");

            var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
            Directory.CreateDirectory(outDir);
            var encode = await runner.RunAsync(new ProcessSpec(Path.Combine(dir, "audiocpp_cli.exe"),
                ["--task", "tts", "--family", "vieneu_v3_turbo", "--model", server.ModelPath("vieneu"), "--backend", "cpu",
                 "--voice-ref", trimmed, "--text", "x",
                 "--session-option", "vieneu_v3_turbo.g2p_library=" + Path.Combine(dir, "sea_g2p_rs.dll"),
                 "--session-option", "vieneu_v3_turbo.g2p_dict=" + Path.Combine(dir, "sea_g2p.bin"),
                 "--request-option", "encode_reference_only=true", "--out-dir", outDir]), null, ct);
            var encoded = Path.Combine(outDir, "vieneu_v3_turbo_reference_codes.txt");
            if (encode.ExitCode != 0 || !File.Exists(encoded))
                throw new InvalidOperationException($"audiocpp_cli không mã hoá được mẫu giọng (mã {encode.ExitCode}): " + string.Join(" | ", encode.OutputTail.TakeLast(3)));

            // Embedding ghi sau cùng: có file embedding nghĩa là codes đã sẵn sàng.
            File.Move(encoded, codes, overwrite: true);
            await File.WriteAllTextAsync(embeddingFile, string.Join(',', response.Embedding.Select(v => v.ToString("F8", CultureInfo.InvariantCulture))), ct);
        }
        catch
        {
            Delete(sample);
            throw;
        }
        finally
        {
            File.Delete(trimmed);
            if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);
        }
    }

    public static void Delete(string sample)
    {
        var (codes, embedding) = Files(sample);
        File.Delete(codes);
        File.Delete(embedding);
    }
}

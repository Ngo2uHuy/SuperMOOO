using SuperMOOO.AI.Audio;
using SuperMOOO.AI.Worker;
using SuperMOOO.AI.Worker.V1;
using SuperMOOO.Core;
using SuperMOOO.Core.Abstractions;
using SuperMOOO.Core.Conventions;

namespace SuperMOOO.AI.Asr;

/// <summary>Whisper CPU int8 chạy trong ai_worker; audio được ffmpeg chuẩn bị thành WAV 16 kHz mono trước khi gửi.</summary>
[Service(As = [typeof(IAsrProvider)])]
public sealed class LocalWhisperAsrProvider(IAiWorkerClient worker, WavPreparer wav, AppPaths paths) : IAsrProvider
{
    public string Id => "local.whisper";
    public bool IsCloud => false;

    public async Task<Transcript> TranscribeAsync(AsrRequest request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        using var prepared = await wav.PrepareAsync(request.AudioPath, 16000, ct);
        var rpc = new TranscribeRequest
        {
            AudioPath = prepared.Path,
            Model = string.IsNullOrWhiteSpace(request.Model) ? "turbo" : request.Model,
            Language = request.Language ?? "",
            Task = "transcribe",
            WordTimestamps = request.WordTimestamps,
            VadFilter = request.VadFilter,
            InitialPrompt = request.Prompt ?? "",
            ModelsDir = paths.Models
        };

        var segments = new List<TranscriptSegment>();
        TranscribeCompleted? completed = null;
        await foreach (var e in worker.TranscribeAsync(rpc, ct))
        {
            switch (e.EventCase)
            {
                case TranscribeEvent.EventOneofCase.Progress:
                    progress?.Report(new ProgressInfo(e.Progress.Fraction, e.Progress.Message));
                    break;
                case TranscribeEvent.EventOneofCase.Segment:
                    var s = e.Segment;
                    segments.Add(new TranscriptSegment(s.Index, TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End), s.Text,
                        s.Words.Count == 0 ? null : [.. s.Words.Select(w =>
                            new WordTiming(TimeSpan.FromSeconds(w.Start), TimeSpan.FromSeconds(w.End), w.Text, w.Probability))]));
                    break;
                case TranscribeEvent.EventOneofCase.Completed:
                    completed = e.Completed;
                    break;
            }
        }

        if (completed is null)
            throw new AiWorkerException(Grpc.Core.StatusCode.DataLoss, "AI worker kết thúc luồng nhận diện mà không báo hoàn tất.");
        return new Transcript(completed.Language, TimeSpan.FromSeconds(completed.Duration), segments);
    }
}

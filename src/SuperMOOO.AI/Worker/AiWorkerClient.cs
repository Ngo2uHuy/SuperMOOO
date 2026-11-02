using System.Runtime.CompilerServices;
using SuperMOOO.AI.Worker.V1;
using SuperMOOO.Core.Conventions;
using Grpc.Core;
using WorkerAsr = SuperMOOO.AI.Worker.V1.Asr;

namespace SuperMOOO.AI.Worker;

public interface IAiWorkerClient
{
    Task<Capabilities> GetCapabilitiesAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> UnloadModelsAsync(IEnumerable<string> modules, CancellationToken ct);
    IAsyncEnumerable<TranscribeEvent> TranscribeAsync(TranscribeRequest request, CancellationToken ct);
    Task<DetectSpeechResponse> DetectSpeechAsync(DetectSpeechRequest request, CancellationToken ct);
    IAsyncEnumerable<TaskEvent> DenoiseAsync(DenoiseRequest request, CancellationToken ct);
    Task<SpeakerEmbeddingResponse> SpeakerEmbeddingAsync(SpeakerEmbeddingRequest request, CancellationToken ct);
    IAsyncEnumerable<TaskEvent> SeparateVocalsAsync(SeparateVocalsRequest request, CancellationToken ct);
    Task<ListVoiceModelsResponse> ListVoiceModelsAsync(ListVoiceModelsRequest request, CancellationToken ct);
    IAsyncEnumerable<TaskEvent> ConvertVoiceAsync(VoiceConvertRequest request, CancellationToken ct);
    Task<LocateSubtitlesResponse> LocateSubtitlesAsync(LocateSubtitlesRequest request, CancellationToken ct);
    IAsyncEnumerable<TextTimelineEvent> TextTimelineAsync(TextTimelineRequest request, CancellationToken ct);
    IAsyncEnumerable<InpaintEvent> InpaintSubtitlesAsync(InpaintSubtitlesRequest request, CancellationToken ct);
}

/// <summary>Bọc client sinh từ proto: giữ worker sống suốt lời gọi, đổi RpcException thành AiWorkerException.</summary>
[Service(As = [typeof(IAiWorkerClient)])]
public sealed class AiWorkerClient(IAiWorkerHost host) : IAiWorkerClient
{
    public Task<Capabilities> GetCapabilitiesAsync(CancellationToken ct) =>
        Unary(invoker => new WorkerControl.WorkerControlClient(invoker)
            .GetCapabilitiesAsync(new GetCapabilitiesRequest(), cancellationToken: ct).ResponseAsync, ct);

    public async Task<IReadOnlyList<string>> UnloadModelsAsync(IEnumerable<string> modules, CancellationToken ct)
    {
        var request = new UnloadModelsRequest();
        request.Modules.AddRange(modules);
        var response = await Unary(invoker => new WorkerControl.WorkerControlClient(invoker)
            .UnloadModelsAsync(request, cancellationToken: ct).ResponseAsync, ct);
        return [.. response.Unloaded];
    }

    public IAsyncEnumerable<TranscribeEvent> TranscribeAsync(TranscribeRequest request, CancellationToken ct) =>
        Stream(invoker => new WorkerAsr.AsrClient(invoker).Transcribe(request, cancellationToken: ct), ct);

    public Task<DetectSpeechResponse> DetectSpeechAsync(DetectSpeechRequest request, CancellationToken ct) =>
        Unary(invoker => new AudioTools.AudioToolsClient(invoker).DetectSpeechAsync(request, cancellationToken: ct).ResponseAsync, ct);

    public IAsyncEnumerable<TaskEvent> DenoiseAsync(DenoiseRequest request, CancellationToken ct) =>
        Stream(invoker => new AudioTools.AudioToolsClient(invoker).Denoise(request, cancellationToken: ct), ct);

    public Task<SpeakerEmbeddingResponse> SpeakerEmbeddingAsync(SpeakerEmbeddingRequest request, CancellationToken ct) =>
        Unary(invoker => new AudioTools.AudioToolsClient(invoker).SpeakerEmbeddingAsync(request, cancellationToken: ct).ResponseAsync, ct);

    public IAsyncEnumerable<TaskEvent> SeparateVocalsAsync(SeparateVocalsRequest request, CancellationToken ct) =>
        Stream(invoker => new AudioTools.AudioToolsClient(invoker).SeparateVocals(request, cancellationToken: ct), ct);

    public Task<ListVoiceModelsResponse> ListVoiceModelsAsync(ListVoiceModelsRequest request, CancellationToken ct) =>
        Unary(invoker => new VoiceConversion.VoiceConversionClient(invoker).ListModelsAsync(request, cancellationToken: ct).ResponseAsync, ct);

    public IAsyncEnumerable<TaskEvent> ConvertVoiceAsync(VoiceConvertRequest request, CancellationToken ct) =>
        Stream(invoker => new VoiceConversion.VoiceConversionClient(invoker).Convert(request, cancellationToken: ct), ct);

    public Task<LocateSubtitlesResponse> LocateSubtitlesAsync(LocateSubtitlesRequest request, CancellationToken ct) =>
        Unary(invoker => new VideoText.VideoTextClient(invoker).LocateSubtitlesAsync(request, cancellationToken: ct).ResponseAsync, ct);

    public IAsyncEnumerable<TextTimelineEvent> TextTimelineAsync(TextTimelineRequest request, CancellationToken ct) =>
        Stream(invoker => new VideoText.VideoTextClient(invoker).TextTimeline(request, cancellationToken: ct), ct);

    public IAsyncEnumerable<InpaintEvent> InpaintSubtitlesAsync(InpaintSubtitlesRequest request, CancellationToken ct) =>
        Stream(invoker => new VideoText.VideoTextClient(invoker).InpaintSubtitles(request, cancellationToken: ct), ct);

    private async Task<T> Unary<T>(Func<CallInvoker, Task<T>> call, CancellationToken ct)
    {
        using var lease = await host.AcquireAsync(ct);
        try { return await call(lease.Invoker); }
        catch (RpcException e) { throw AiWorkerException.From(e, ct); }
    }

    private async IAsyncEnumerable<T> Stream<T>(Func<CallInvoker, AsyncServerStreamingCall<T>> start,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var lease = await host.AcquireAsync(ct);
        using var call = start(lease.Invoker);
        while (true)
        {
            // yield không được nằm trong try có catch, nên MoveNext bọc riêng.
            bool hasNext;
            try { hasNext = await call.ResponseStream.MoveNext(ct); }
            catch (RpcException e) { throw AiWorkerException.From(e, ct); }
            if (!hasNext) yield break;
            yield return call.ResponseStream.Current;
        }
    }
}

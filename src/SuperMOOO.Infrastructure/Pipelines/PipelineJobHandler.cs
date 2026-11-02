using Microsoft.Extensions.Logging;

namespace SuperMOOO.Infrastructure.Pipelines;

/// <summary>Job `pipeline`: payload `{ "pipeline": "&lt;id&gt;", "input": { ... } }`.</summary>
public sealed class PipelineJobHandler(PipelineDefinitionLoader loader, PipelineRunner runner,
    IEnumerable<IPipelineCompletionObserver> observers, ILogger<PipelineJobHandler> log) : IJobHandler
{
    public string Type => JobTypes.Pipeline;

    public async Task<JsonNode?> RunAsync(JobContext context, CancellationToken ct)
    {
        var payload = context.Payload.AsObject();
        var id = payload["pipeline"]?.GetValue<string>() ?? throw new InvalidOperationException("Payload thiếu trường 'pipeline'.");
        var input = payload["input"] as JsonObject ?? [];
        var result = await runner.RunAsync(context.JobId, loader.Get(id), input, context.WorkFolder, context.Progress, ct);
        foreach (var observer in observers)
        {
            try { await observer.OnCompletedAsync(new PipelineCompletion(context.JobId, id, input, result), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning(ex, "Observer {Observer} lỗi sau pipeline {Pipeline} (job {Job})", observer.GetType().Name, id, context.JobId); }
        }

        return result;
    }
}

namespace SuperMOOO.Core.Pipelines;

public sealed record StepDefinition(string Id, string Kind, string? When = null, JsonObject? Options = null);
public sealed record PipelineDefinition(string Id, string Title, IReadOnlyList<StepDefinition> Steps);
public sealed class PipelineState : Dictionary<string, string>;          // đường dẫn/JSON giữa các bước; lưu job_step.state_json để resume
public sealed record StepContext(string JobId, string WorkFolder, JsonObject Input, PipelineState State, IProgress<ProgressInfo> Progress);
public sealed record StepOutput(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Files);

public interface IPipelineStep
{
    string Kind { get; }                        // danh sách §2.2
    Type OptionsType { get; }                   // record options; bind từ JSON + validate DataAnnotations
    bool IsCloudBilled { get; }                 // true: lỗi sau khi đã gửi → WaitingManualRetry, không tự retry
    bool IsCloudBilledFor(object options) => IsCloudBilled;   // tính phí hay không còn tuỳ options (vd. nguồn ảnh miễn phí)
    Task<StepOutput> ExecuteAsync(StepContext context, object options, CancellationToken ct);
}

public abstract class PipelineStep<TOptions> : IPipelineStep where TOptions : class, new()
{
    public abstract string Kind { get; }
    public Type OptionsType => typeof(TOptions);
    public virtual bool IsCloudBilled => false;
    protected virtual bool IsCloudBilledFor(TOptions options) => IsCloudBilled;
    bool IPipelineStep.IsCloudBilledFor(object options) => IsCloudBilledFor((TOptions)options);
    public Task<StepOutput> ExecuteAsync(StepContext context, object options, CancellationToken ct) => RunAsync(context, (TOptions)options, ct);
    protected abstract Task<StepOutput> RunAsync(StepContext context, TOptions options, CancellationToken ct);
}

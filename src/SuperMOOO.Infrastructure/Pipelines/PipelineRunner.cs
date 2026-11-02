using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using SuperMOOO.Data.Repositories.Jobs;

namespace SuperMOOO.Infrastructure.Pipelines;

/// <summary>Chạy tuần tự các bước của pipeline, checkpoint từng bước vào job_step để retry/resume bỏ qua bước đã xong.</summary>
[Service]
public sealed class PipelineRunner
{
    private readonly Dictionary<string, IPipelineStep> _steps;
    private readonly JobRepository _repo;
    private readonly ISettingsStore _settings;

    public PipelineRunner(IEnumerable<IPipelineStep> steps, JobRepository repo, ISettingsStore settings)
    {
        _repo = repo;
        _settings = settings;
        _steps = steps.GroupBy(s => s.Kind).ToDictionary(g => g.Key, g => g.Count() == 1
            ? g.Single()
            : throw new InvalidOperationException($"Trùng IPipelineStep cho kind '{g.Key}'."));
    }

    public async Task<JsonNode> RunAsync(string jobId, PipelineDefinition definition, JsonObject input, string workFolder,
        IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        Validate(definition);
        var previous = (await _repo.GetStepsAsync(jobId, ct)).ToDictionary(s => s.Idx);
        var defaults = _settings.Get<PipelineDefaultsSettings>("pipeline.defaults");
        var state = new PipelineState();
        var files = new List<string>();

        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var def = definition.Steps[i];
            var step = _steps[def.Kind];
            previous.TryGetValue(i, out var prior);

            if (prior is { Status: JobStatus.Succeeded })
            {
                Restore(prior.StateJson, state, files);
                continue;
            }

            if (def.When is { } flag && !(input[flag] is JsonValue v && v.TryGetValue<bool>(out var on) && on))
            {
                await SaveAsync(jobId, i, def, step, JobStatus.Succeeded, prior, state, files, "bỏ qua (when=false)", ct);
                continue;
            }

            await SaveAsync(jobId, i, def, step, JobStatus.Running, prior, state, files, null, ct);
            var scaled = new StepProgress(progress, i, definition.Steps.Count, def.Id);
            var billed = step.IsCloudBilled;
            try
            {
                var options = BindOptions(step, def, defaults, input);
                billed = step.IsCloudBilledFor(options);
                var output = await step.ExecuteAsync(new StepContext(jobId, workFolder, input, state, scaled), options, ct);
                foreach (var (key, value) in output.Values) state[key] = value;
                files.AddRange(output.Files);
            }
            catch (OperationCanceledException)
            {
                await SaveAsync(jobId, i, def, step, JobStatus.Cancelled, prior, state, files, "đã huỷ", CancellationToken.None);
                throw;
            }
            catch (Exception ex)
            {
                await SaveAsync(jobId, i, def, step, JobStatus.Failed, prior, state, files, ex.Message, CancellationToken.None);
                if (billed)
                    throw new JobWaitingManualRetryException($"Bước '{def.Id}' (cloud, tính phí) lỗi: {ex.Message}. Kiểm tra rồi thử lại thủ công.", ex);
                throw;
            }

            await SaveAsync(jobId, i, def, step, JobStatus.Succeeded, prior, state, files, null, ct);
            progress.Report(new ProgressInfo((i + 1.0) / definition.Steps.Count, def.Id));
        }

        return Snapshot(state, files);
    }

    private void Validate(PipelineDefinition definition)
    {
        var unknown = definition.Steps.Where(s => !_steps.ContainsKey(s.Kind)).Select(s => $"'{s.Id}' ({s.Kind})").ToList();
        if (unknown.Count > 0)
            throw new InvalidOperationException($"Pipeline '{definition.Id}' dùng bước chưa được cài đặt: {string.Join(", ", unknown)}.");

        var duplicate = definition.Steps.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Pipeline '{definition.Id}' có id bước trùng '{duplicate.Key}'.");
    }

    // Thứ tự gộp (sau ghi đè trước): định nghĩa → mặc định trong settings → input.stepOptions.<stepId>.
    private static object BindOptions(IPipelineStep step, StepDefinition def, PipelineDefaultsSettings defaults, JsonObject input)
    {
        var merged = new JsonObject();
        Merge(merged, def.Options);
        Merge(merged, defaults.GetValueOrDefault(def.Kind));
        Merge(merged, (input["stepOptions"] as JsonObject)?[def.Id] as JsonObject);

        var options = merged.Deserialize(step.OptionsType, PipelineJson.Options)!;
        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(options, new ValidationContext(options), errors, validateAllProperties: true))
            throw new ValidationException($"Tuỳ chọn bước '{def.Id}' không hợp lệ: {string.Join("; ", errors.Select(e => e.ErrorMessage))}");
        return options;
    }

    private static void Merge(JsonObject target, JsonObject? source)
    {
        if (source is null) return;
        foreach (var (key, value) in source) target[key] = value?.DeepClone();
    }

    private Task SaveAsync(string jobId, int index, StepDefinition def, IPipelineStep step, JobStatus status, JobStepRecord? prior,
        PipelineState state, List<string> files, string? error, CancellationToken ct) =>
        _repo.UpsertStepAsync(jobId, new JobStepRecord(index, def.Id, def.Kind, status, step.IsCloudBilled,
            (prior?.Attempts ?? 0) + (status == JobStatus.Running ? 1 : 0),
            status == JobStatus.Succeeded ? Snapshot(state, files).ToJsonString() : null, error), ct);

    private static JsonObject Snapshot(PipelineState state, List<string> files) => new()
    {
        ["state"] = JsonSerializer.SerializeToNode(state),
        ["files"] = JsonSerializer.SerializeToNode(files)
    };

    private static void Restore(string? stateJson, PipelineState state, List<string> files)
    {
        if (stateJson is null || JsonNode.Parse(stateJson) is not JsonObject snapshot) return;
        state.Clear();
        foreach (var (key, value) in snapshot["state"]?.AsObject() ?? [])
            state[key] = value?.GetValue<string>() ?? "";
        files.Clear();
        files.AddRange(snapshot["files"]?.AsArray().Select(f => f!.GetValue<string>()) ?? []);
    }

    private sealed class StepProgress(IProgress<ProgressInfo> parent, int index, int count, string stepId) : IProgress<ProgressInfo>
    {
        public void Report(ProgressInfo value)
        {
            var fraction = value.Fraction < 0 ? -1 : (index + Math.Clamp(value.Fraction, 0, 1)) / count;
            parent.Report(new ProgressInfo(fraction, $"{stepId}: {value.Message}"));
        }
    }
}

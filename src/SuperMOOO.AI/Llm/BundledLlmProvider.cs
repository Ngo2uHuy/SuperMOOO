using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace SuperMOOO.AI.Llm;

/// <summary>
/// LLM chạy máy: Qwen3 1.7B Q4_K_M (GGUF) qua llama-server đóng gói trong tools\llama_cpp. Một slot (mỗi lúc một suy luận),
/// tự tắt khi rảnh hoặc khi RAM cạn (LlamaServerProcess). Tách khỏi provider "local" (Ollama/LM Studio).
/// </summary>
[Service(As = [typeof(ILlmProvider)])]
public sealed partial class BundledLlmProvider : ILlmProvider, IAsyncDisposable
{
    public const string ModelName = "qwen3-1.7b";
    public const string ModelFile = "Qwen3-1.7B-Q4_K_M.gguf";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(90);
    // Model nhỏ nên chỉ giữ ~1,5 GB RAM; rảnh 5 phút thì nhả để nhường máy cho TTS/render.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private readonly AppPaths _paths;
    private readonly ISettingsStore _settings;
    private readonly IRuntimeResources? _resources;
    private readonly LlamaServerProcess _server;

    public BundledLlmProvider(IProcessRunner runner, AppPaths paths, ISettingsStore settings, IRuntimeResources? resources = null)
    {
        _paths = paths;
        _settings = settings;
        _resources = resources;
        _server = new LlamaServerProcess(Id, runner, resources, IdleTimeout);
    }

    public string Id => LlmProviderSpec.Bundled.Id;

    public static string ModelPathOf(AppPaths paths) => Path.Combine(paths.Models, "llm", "Qwen3-1.7B", ModelFile);
    public string ModelPath => ModelPathOf(_paths);
    private string ServerExe => Path.Combine(_paths.ToolsDir, "llama_cpp", "llama-server.exe");
    public bool IsAvailable => File.Exists(ServerExe) && File.Exists(ModelPath);

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        await _server.UseLock.WaitAsync(ct);
        try
        {
            var port = await _server.EnsureStartedAsync(ServerSpec(), ct);
            var payload = new JsonObject
            {
                ["messages"] = new JsonArray(request.Messages.Select(m => (JsonNode)new JsonObject
                {
                    ["role"] = m.Role.ToString().ToLowerInvariant(), ["content"] = m.Content
                }).ToArray()),
                ["temperature"] = request.Temperature, ["max_tokens"] = request.MaxTokens, ["stream"] = false,
                // Qwen3 mặc định suy nghĩ dài: tắt để trả lời bình luận kịp nhịp live.
                ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false }
            };
            if (request.JsonOutput) payload["response_format"] = new JsonObject { ["type"] = "json_object" };

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            try
            {
                using var response = await _server.Http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/chat/completions", payload, cts.Token);
                var body = await response.Content.ReadAsStringAsync(cts.Token);
                if (!response.IsSuccessStatusCode)
                    throw new ProviderException(Id, ProviderErrorKind.BadResponse, $"llama-server HTTP {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");

                var root = JsonNode.Parse(body);
                var choice = root?["choices"]?[0];
                var text = ThinkBlock().Replace(choice?["message"]?["content"]?.GetValue<string>() ?? "", "").Trim();
                if (choice?["finish_reason"]?.GetValue<string>() == "length")
                    throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Model local trả nội dung chưa hoàn tất (hết max_tokens).");
                if (text.Length == 0) throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Model local trả nội dung rỗng.");
                return new LlmResponse(text, ModelName, root?["usage"]?["prompt_tokens"]?.GetValue<int>(), root?["usage"]?["completion_tokens"]?.GetValue<int>());
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Quá thời gian: llama-server vẫn đang sinh token nên phải dừng hẳn.
                await _server.StopAsync();
                throw new ProviderException(Id, ProviderErrorKind.Timeout, "Model local quá thời gian.");
            }
            catch (OperationCanceledException)
            {
                await _server.StopAsync();
                throw;
            }
            catch (HttpRequestException)
            {
                await _server.StopAsync();
                throw new ProviderException(Id, ProviderErrorKind.Network, "Mất kết nối llama-server; sẽ khởi động lại ở lần gọi sau.");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Model local trả dữ liệu không đọc được.");
            }
        }
        finally
        {
            _server.Touch();
            _server.UseLock.Release();
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(IsAvailable ? [ModelName] : []);

    // Không khởi động server chỉ để kiểm tra: đủ file là coi như sẵn sàng.
    public Task<ProviderCheckResult> CheckAsync(CancellationToken ct) => Task.FromResult(
        !File.Exists(ServerExe) ? new ProviderCheckResult(false, ProviderErrorKind.KeyMissing, "Chưa có tools\\llama_cpp\\llama-server.exe.")
        : !File.Exists(ModelPath) ? new ProviderCheckResult(false, ProviderErrorKind.KeyMissing, $"Chưa có model: {ModelPath}. Tải ở Cài đặt → Livestream (LLM).")
        : new ProviderCheckResult(true, null, $"Model local sẵn sàng: {ModelFile}."));

    private LlamaServerSpec ServerSpec()
    {
        var ctx = Math.Clamp(_settings.Get<LiveSettings>("live").NumCtx, 1024, 16384);
        var threads = _resources?.Plan.AiThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        return new LlamaServerSpec(ServerExe, ModelPath, "model LLM local", port =>
        [
            "-m", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(), "-t", threads.ToString(),
            "-np", "1", "-c", ctx.ToString(), "--no-webui"
        ]);
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

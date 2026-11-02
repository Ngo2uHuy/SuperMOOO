using System.Net.Http.Json;
using System.Text.Json.Nodes;
using SuperMOOO.AI.Catalog;
using SuperMOOO.AI.Llm;

namespace SuperMOOO.AI.Translation;

/// <summary>Dịch offline bằng Hy-MT2 (GGUF) qua llama-server: khởi động khi cần, cổng trống ngẫu nhiên, dịch song song nhiều slot kèm vài câu nguồn trước đó làm ngữ cảnh, dừng bằng Job Object.</summary>
[Service(As = [typeof(ITranslator)])]
public sealed class LocalMtTranslator : ITranslator, IAsyncDisposable
{
    private const int ContextSentences = 3;
    private const int MaxChars = 1500;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    // llama-server giữ ~2 GB model: rảnh lâu thì tắt, lần dịch sau tự khởi động lại.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(10);

    // Tên đích cho mẫu prompt tiếng Anh / mẫu có ngữ cảnh (tiếng Trung) của Hunyuan-MT.
    private static readonly Dictionary<string, (string En, string Zh)> Languages = new()
    {
        ["vi"] = ("Vietnamese", "越南语"), ["ja"] = ("Japanese", "日语"), ["ko"] = ("Korean", "韩语"), ["en"] = ("English", "英语"),
        ["zh"] = ("Chinese", "中文"), ["zh-TW"] = ("Traditional Chinese", "繁体中文"), ["fr"] = ("French", "法语"), ["de"] = ("German", "德语"),
        ["es"] = ("Spanish", "西班牙语"), ["pt"] = ("Portuguese", "葡萄牙语"), ["ru"] = ("Russian", "俄语"), ["it"] = ("Italian", "意大利语"),
        ["id"] = ("Indonesian", "印度尼西亚语"), ["th"] = ("Thai", "泰语"), ["tr"] = ("Turkish", "土耳其语")
    };

    private readonly AppPaths _paths;
    private readonly IRuntimeResources? _resources;
    private readonly LlamaServerProcess _server;

    public LocalMtTranslator(IProcessRunner runner, AppPaths paths, IRuntimeResources? resources = null)
    {
        _paths = paths;
        _resources = resources;
        _server = new LlamaServerProcess(Id, runner, resources, IdleTimeout);
    }

    public string Id => "local-mt";
    public bool RequiresKey => false;

    public string ModelPath => Path.Combine(_paths.Models, "translate", "Hy-MT2", "Hy-MT2-1.8B-Q4_K_M.gguf");
    private string ServerExe => Path.Combine(_paths.ToolsDir, "llama_cpp", "llama-server.exe");

    public bool IsAvailable => File.Exists(ServerExe) && File.Exists(ModelPath);

    public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, TranslateOptions options, CancellationToken ct)
    {
        var target = LanguageCatalog.LocaleCode(options.TargetLanguage);
        if (target == "auto") throw new ArgumentException("Chọn ngôn ngữ đích cụ thể để dịch.");
        var name = TargetName(target);

        var result = new string[texts.Count];
        Array.Fill(result, "");
        await _server.UseLock.WaitAsync(ct);
        try
        {
            // Ngữ cảnh chỉ gồm câu nguồn nên các câu độc lập: dựng sẵn prompt rồi gửi song song tới các slot của llama-server.
            var context = new Queue<string>();
            var work = new List<(int Index, List<(string Prompt, int Length)> Chunks)>();
            for (var i = 0; i < texts.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(texts[i])) continue;
                var text = texts[i].Trim();
                work.Add((i, TextChunker.Split(text, MaxChars).Select(c => (BuildPrompt(c, name, context), c.Length)).ToList()));

                context.Enqueue(text);
                if (context.Count > ContextSentences) context.Dequeue();
            }

            using var slots = new SemaphoreSlim(Parallelism);
            await Task.WhenAll(work.Select(async w =>
            {
                await slots.WaitAsync(ct);
                try
                {
                    var parts = new List<string>();
                    foreach (var (prompt, length) in w.Chunks) parts.Add(await TranslateOneAsync(prompt, length, ct));
                    result[w.Index] = string.Join(' ', parts);
                }
                finally { slots.Release(); }
            }));
        }
        finally
        {
            _server.Touch();
            _server.UseLock.Release();
        }

        return result;
    }

    // Có ngữ cảnh dùng mẫu chính thức của Hunyuan-MT; câu đầu dùng mẫu mặc định của model card Hy-MT2.
    private static string BuildPrompt(string text, (string En, string Zh) target, Queue<string> context)
    {
        if (context.Count > 0)
            return $"{string.Join('\n', context)}\n参考上面的信息，把下面的文本翻译成{target.Zh}，注意不需要翻译上文，也不需要额外解释：\n\n{text}";
        return target.En == "Chinese"
            ? $"将以下文本翻译为中文，注意只需要输出翻译后的结果，不要额外解释：\n\n{text}"
            : $"Translate the following text into {target.En}.\nNote that you should only output the translated result without any additional explanation:\n\n{text}";
    }

    private static (string En, string Zh) TargetName(string target) =>
        Languages.TryGetValue(target, out var n) ? n
        : Languages.TryGetValue(target.Split('-')[0], out n) ? n
        : throw new ArgumentException($"Bộ dịch offline chưa hỗ trợ ngôn ngữ đích '{target}'.");

    // Số slot song song theo số luồng AI; mỗi slot giữ ngữ cảnh 2048 như trước.
    private int Parallelism => Math.Clamp(Threads / 2, 2, 4);
    private int Threads => _resources?.Plan.AiThreads ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 6);

    private async Task<string> TranslateOneAsync(string prompt, int sourceLength, CancellationToken ct)
    {
        var port = await _server.EnsureStartedAsync(ServerSpec(), ct);
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            ["temperature"] = 0.7, ["top_p"] = 0.6, ["top_k"] = 20, ["repeat_penalty"] = 1.05,
            ["max_tokens"] = Math.Min(1024, sourceLength * 4 + 64), ["stream"] = false
        };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _server.Http.PostAsJsonAsync($"http://127.0.0.1:{port}/v1/chat/completions", payload, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            if (!response.IsSuccessStatusCode)
                throw new ProviderException(Id, ProviderErrorKind.BadResponse, $"llama-server HTTP {(int)response.StatusCode}: {(body.Length > 300 ? body[..300] : body)}");

            var text = JsonNode.Parse(body)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(text)) throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Bộ dịch offline trả bản dịch rỗng.");
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException(Id, ProviderErrorKind.Timeout, "Bộ dịch offline quá thời gian.");
        }
        catch (OperationCanceledException)
        {
            // Huỷ khi yêu cầu đã gửi: llama-server vẫn tiếp tục sinh token nên phải dừng; lần dịch sau tự khởi động lại.
            await _server.StopAsync();
            throw;
        }
        catch (HttpRequestException)
        {
            await _server.StopAsync();
            throw new ProviderException(Id, ProviderErrorKind.Network, "Mất kết nối llama-server; sẽ khởi động lại ở lần dịch sau.");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            throw new ProviderException(Id, ProviderErrorKind.BadResponse, "Bộ dịch offline trả dữ liệu không đọc được.");
        }
    }

    private LlamaServerSpec ServerSpec() => new(ServerExe, ModelPath, "model dịch offline", port =>
    [
        "-m", ModelPath, "--host", "127.0.0.1", "--port", port.ToString(), "-t", Threads.ToString(),
        "-np", Parallelism.ToString(), "-c", (2048 * Parallelism).ToString(), "--no-webui"
    ]);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

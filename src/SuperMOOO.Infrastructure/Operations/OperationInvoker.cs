using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace SuperMOOO.Infrastructure.Operations;

/// <summary>Gọi operation theo key (hoặc key cũ): bind JSON → request, validate DataAnnotations, chạy handler, trả kết quả dạng JSON.</summary>
[Service]
public sealed class OperationInvoker(OperationRegistry registry, IServiceProvider services)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task<JsonNode?> InvokeAsync(string key, JsonObject request, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var descriptor = registry.Find(key) ?? throw new KeyNotFoundException($"Không có operation '{key}'.");
        var bound = request.Deserialize(descriptor.RequestType, Json)
            ?? throw new ArgumentException($"Request của '{key}' rỗng.", nameof(request));

        var errors = new List<ValidationResult>();
        if (!Validator.TryValidateObject(bound, new ValidationContext(bound), errors, validateAllProperties: true))
            throw new ValidationException(string.Join("; ", errors.Select(e => e.ErrorMessage)));

        var handler = services.GetRequiredService(descriptor.HandlerType);
        var handle = typeof(IOperationHandler<,>).MakeGenericType(descriptor.RequestType, descriptor.ResultType).GetMethod("HandleAsync")!;
        // DoNotWrapExceptions: lỗi ném đồng bộ trong handler hiện ra nguyên gốc thay vì TargetInvocationException.
        var task = (Task)handle.Invoke(handler, BindingFlags.DoNotWrapExceptions, null, [bound, progress, ct], null)!;
        await task;

        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return JsonSerializer.SerializeToNode(result, descriptor.ResultType, Json);
    }
}

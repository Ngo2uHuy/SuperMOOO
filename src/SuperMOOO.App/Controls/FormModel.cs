using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperMOOO.Core.Operations;
using SuperMOOO.Infrastructure.Operations;

namespace SuperMOOO.App.Controls;

public sealed partial class FormFieldViewModel(OperationField meta) : ObservableObject
{
    public OperationField Meta => meta;
    public string Label => meta.Label;
    public string? Help => string.IsNullOrEmpty(meta.Example) ? meta.Help : $"{meta.Help} Ví dụ: {meta.Example}".Trim();

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    public partial bool Flag { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }
}

/// <summary>Trạng thái của form động: field sinh từ [Field], đóng gói thành request qua JSON rồi kiểm DataAnnotations.</summary>
public sealed class FormModel
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly Type _requestType;

    public FormModel(Type requestType, object? initial = null)
    {
        _requestType = requestType;
        Fields = FieldReflector.Describe(requestType).Select(f => new FormFieldViewModel(f)).ToList();
        if (initial is null) return;

        var values = JsonSerializer.SerializeToNode(initial, requestType, Json) as JsonObject ?? [];
        foreach (var field in Fields)
        {
            if (!values.TryGetPropertyValue(JsonNamingPolicy.CamelCase.ConvertName(field.Meta.Name), out var node) || node is null) continue;
            if (field.Meta.Kind == FieldKind.Bool) field.Flag = node.GetValue<bool>();
            else field.Text = node is JsonArray array ? string.Join('\n', array.Select(i => i?.ToString())) : node.ToString();
        }
    }

    public IReadOnlyList<FormFieldViewModel> Fields { get; }

    public string? GeneralError { get; private set; }

    public bool TryBuild(out object? request)
    {
        GeneralError = null;
        var json = new JsonObject();
        foreach (var field in Fields)
        {
            field.Error = null;
            if (field.Meta.Kind == FieldKind.Bool) json[field.Meta.Name] = field.Flag;
            else if (ToNode(field) is { } node) json[field.Meta.Name] = node;
            else if (field.Error is null && field.Meta.Required) field.Error = "Bắt buộc nhập.";
        }

        request = null;
        if (Fields.Any(f => f.Error is not null)) return false;

        try
        {
            request = json.Deserialize(_requestType, Json);
        }
        catch (JsonException ex)
        {
            GeneralError = ex.Message;
            return false;
        }

        var errors = new List<ValidationResult>();
        if (Validator.TryValidateObject(request!, new ValidationContext(request!), errors, validateAllProperties: true)) return true;

        foreach (var error in errors)
        {
            var field = Fields.FirstOrDefault(f => error.MemberNames.Contains(f.Meta.Name));
            if (field is not null) field.Error = error.ErrorMessage;
            else GeneralError = error.ErrorMessage;
        }

        request = null;
        return false;
    }

    private static JsonNode? ToNode(FormFieldViewModel field)
    {
        var text = field.Text.Trim();
        if (text.Length == 0) return null;

        switch (field.Meta.Kind)
        {
            case FieldKind.Choice when !field.Meta.Choices.Contains(text):
                field.Error = "Chọn một giá trị trong danh sách.";
                return null;
            case FieldKind.Number:
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return JsonValue.Create(number);
                field.Error = "Không phải số.";
                return null;
            case FieldKind.DateTime:
                if (DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var date)) return JsonValue.Create(date.ToString("o"));
                field.Error = "Ngày giờ không hợp lệ.";
                return null;
            case FieldKind.Url when !IsHttpUrl(text):
                field.Error = "Chưa phải liên kết http(s).";
                return null;
            case FieldKind.UrlList or FieldKind.MultiLine when field.Meta.ClrType != typeof(string):
                var lines = Lines(field.Text);
                if (field.Meta.Kind == FieldKind.UrlList && lines.FirstOrDefault(l => !IsHttpUrl(l)) is { } bad)
                {
                    field.Error = $"Dòng không phải liên kết http(s): {bad}";
                    return null;
                }

                return new JsonArray(lines.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray());
            default:
                return JsonValue.Create(field.Meta.Kind is FieldKind.MultiLine or FieldKind.UrlList ? field.Text.Trim() : text);
        }
    }

    private static string[] Lines(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

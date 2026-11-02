using System.Collections.ObjectModel;

namespace SuperMOOO.App.Features.S16;

public sealed record ApiKeyRow(string Provider, string Name, string Hint, string Status);

[Service(ServiceLifetime.Transient)]
public sealed partial class ApiKeysViewModel : ObservableObject
{
    private const string Scope = "ai";

    private readonly ISecretStore _secrets;

    public ApiKeysViewModel(ISecretStore secrets)
    {
        _secrets = secrets;
        SelectedProvider = Providers[0];
    }

    public IReadOnlyList<string> Providers => ApiKeyValidator.Providers;
    public ObservableCollection<ApiKeyRow> Keys { get; } = [];

    [ObservableProperty]
    public partial string SelectedProvider { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    public async Task LoadAsync()
    {
        var items = await _secrets.ListAsync(Scope);
        Keys.Clear();
        foreach (var info in items.Where(i => i.Key.Scope == Scope))
            Keys.Add(new ApiKeyRow(info.Key.Name.Split('#')[0], info.Key.Name, info.Hint, info.Status));
    }

    /// <summary>Lưu key mới (nhiều key/provider, đánh số #n). Key sai định dạng vẫn được lưu nhưng gắn invalid_format.</summary>
    public async Task<ApiKeyCheck> AddAsync(string key)
    {
        var provider = SelectedProvider;
        var check = ApiKeyValidator.Validate(provider, key);
        if (string.IsNullOrWhiteSpace(key))
        {
            Status = check.Message;
            return check;
        }

        var next = Keys.Where(k => k.Provider == provider)
            .Select(k => int.TryParse(k.Name.Split('#').ElementAtOrDefault(1), out var n) ? n : -1)
            .DefaultIfEmpty(-1).Max() + 1;
        await _secrets.SetAsync(new SecretKey(Scope, $"{provider}#{next}"), key.Trim(), check.Status);
        await LoadAsync();

        Status = check.Ok ? "Đã lưu key." : $"Đã lưu nhưng đánh dấu sai định dạng: {check.Message}";
        return check;
    }

    [RelayCommand]
    private async Task DeleteAsync(ApiKeyRow row)
    {
        await _secrets.DeleteAsync(new SecretKey(Scope, row.Name));
        await LoadAsync();
        Status = "Đã xoá key.";
    }
}

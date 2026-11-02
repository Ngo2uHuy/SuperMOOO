using System.Security.Cryptography;
using System.Text;

namespace SuperMOOO.Workflows.Projects;

/// <summary>
/// Chỉ mục dự án (bảng project): mỗi video nguồn có một thư mục kết quả; S02 ghi phụ đề đã chỉnh vào đó để S01 dùng lại.
/// Không migrate dự án cũ, chỉ index thư mục do app tạo.
/// </summary>
[Service]
public sealed class ProjectIndex(IProjectStore store, ISettingsStore settings)
{
    public const string Localize = "localize";
    public const string Subtitle = "subtitle";
    public const string Reup = "reup";
    public const string Review = "review";
    public const string Youtube = "youtube";
    public const string Talkshow = "talkshow";

    public string OutputRoot()
    {
        var root = settings.Get<PathsSettings>("paths").OutputRoot;
        return string.IsNullOrWhiteSpace(root) ? AppPaths.DefaultOutputRoot : root;
    }

    /// <summary>Thư mục dự án cho một nguồn; nguồn khác đường dẫn nhưng trùng tên thì thêm mã ngắn để không ghi đè nhau.</summary>
    public async Task<string> FolderForAsync(string kind, string source, CancellationToken ct = default)
    {
        var stem = SafeName(Auto.SourceKinds.NameOf(source));
        var folder = Path.Combine(OutputRoot(), char.ToUpperInvariant(kind[0]) + kind[1..], stem);
        if (await store.GetByFolderAsync(folder, ct) is { } existing && SourceOf(existing) is { } other
            && !string.Equals(Path.GetFullPath(other), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            folder += "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(source).ToLowerInvariant())))[..6];
        return folder;
    }

    /// <summary>Danh sách dự án còn tồn tại trên đĩa; dự án có thư mục đã bị xoá thì bị loại khỏi chỉ mục.</summary>
    public async Task<IReadOnlyList<ProjectRecord>> ScanAsync(string kind, CancellationToken ct = default)
    {
        var alive = new List<ProjectRecord>();
        foreach (var p in await store.ListAsync(kind, ct))
        {
            if (Directory.Exists(p.Folder)) alive.Add(p);
            else await store.DeleteAsync(p.Id, ct);
        }

        return alive;
    }

    public async Task<JsonObject> GetStateAsync(string folder, CancellationToken ct = default) =>
        await store.GetByFolderAsync(folder, ct) is { StateJson: { } json } && JsonNode.Parse(json) is JsonObject state ? state : [];

    public async Task SaveAsync(string kind, string name, string folder, Action<JsonObject> update, CancellationToken ct = default)
    {
        var existing = await store.GetByFolderAsync(folder, ct);
        var state = existing?.StateJson is { } json && JsonNode.Parse(json) is JsonObject s ? s : [];
        update(state);
        Directory.CreateDirectory(folder);
        await store.UpsertAsync(new ProjectRecord(existing?.Id ?? Guid.NewGuid().ToString("N"), kind, name, folder, state.ToJsonString(), DateTimeOffset.UtcNow), ct);
    }

    public static string? SourceOf(ProjectRecord p) =>
        p.StateJson is { } json && JsonNode.Parse(json) is JsonObject o ? o["source"]?.GetValue<string>() : null;

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "project" : clean.Length > 80 ? clean[..80] : clean;
    }
}

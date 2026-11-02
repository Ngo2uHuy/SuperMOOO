using System.Text.Json;

namespace SuperMOOO.Workflows.Timeline;

public static class TimelineProjectFile
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static async Task SaveAsync(TimelineProject project, string path, CancellationToken ct = default)
    {
        TimelineEditing.Validate(project);
        path = Path.GetFullPath(path);
        if (!Path.GetExtension(path).Equals(".aivtimeline", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Dự án timeline phải dùng đuôi .aivtimeline.");
        if (project.Clips.Any(c => c.Track != TimelineTrack.Subtitle && Path.GetFullPath(c.Path).Equals(path, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Không được lưu dự án đè lên file nguồn.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(project, Json), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static async Task<TimelineProject> LoadAsync(string path, CancellationToken ct = default)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("File dự án quá lớn.");
        try
        {
            var project = JsonSerializer.Deserialize<TimelineProject>(await File.ReadAllTextAsync(path, ct), Json)
                ?? throw new InvalidDataException("File dự án rỗng.");
            TimelineEditing.Validate(project);
            var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
            return project with { Clips = project.Clips.Select(c => c.Track == TimelineTrack.Subtitle ? c :
                c with { Path = Path.GetFullPath(c.Path, folder) }).ToArray() };
        }
        catch (JsonException ex) { throw new InvalidDataException("File dự án timeline không hợp lệ.", ex); }
    }
}

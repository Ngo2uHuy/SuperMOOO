using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Jobs;
using SuperMOOO.Core.Publishing;
using SuperMOOO.Infrastructure.Health;
using SuperMOOO.Infrastructure.Operations;

namespace SuperMOOO.App.Features.S16;

public sealed record BackupRow(string Path, string Name, string Info, bool Valid);

public sealed record DiskRow(string Name, string Detail);

public sealed record FolderRow(string Name, string Size, string Note);

[Service(ServiceLifetime.Transient)]
public sealed partial class BackupViewModel(BackupService backups, StorageService storage, DiskMonitor disks, CacheJanitor cache, ISettingsStore settings,
    IPublishSettingsStore publish, IJobQueue queue, IDialogService dialogs) : ObservableObject
{
    private static readonly Dictionary<string, string> FolderNotes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["models"] = "Model AI (không nằm trong sao lưu)", ["cache"] = "Cache, dọn được (không nằm trong sao lưu)", ["worker"] = "Worker Python + venv (không nằm trong sao lưu)",
        ["lipsync"] = "Avatar (có sao lưu) + MuseTalk/venv (không sao lưu)", ["voices"] = "Giọng clone, giọng neo", ["logs"] = "Nhật ký", ["backups"] = "Các bản sao lưu",
        ["legacy"] = "Dữ liệu nhập từ bản cũ", ["pipelines"] = "Định nghĩa pipeline", ["aivs.db"] = "Cơ sở dữ liệu + cài đặt"
    };

    public ObservableCollection<BackupRow> Backups { get; } = [];
    public ObservableCollection<DiskRow> Disks { get; } = [];
    public ObservableCollection<FolderRow> Folders { get; } = [];

    [ObservableProperty]
    public partial BackupRow? SelectedBackup { get; set; }

    [ObservableProperty]
    public partial bool IncludeMedia { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial string? PendingText { get; set; }

    [ObservableProperty]
    public partial int WarnFreeGb { get; set; } = 5;

    [ObservableProperty]
    public partial int MinFreeMb { get; set; } = 1024;

    [ObservableProperty]
    public partial int LogKeepDays { get; set; } = 14;

    [ObservableProperty]
    public partial string BackupDir { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoBackup { get; set; }

    [ObservableProperty]
    public partial int AutoBackupEveryHours { get; set; } = 24;

    [ObservableProperty]
    public partial int AutoBackupKeep { get; set; } = 7;

    [ObservableProperty]
    public partial bool AutoBackupIncludeMedia { get; set; }

    public async Task LoadAsync()
    {
        var s = settings.Get<StorageSettings>("storage");
        (WarnFreeGb, LogKeepDays, BackupDir, AutoBackup, AutoBackupEveryHours, AutoBackupKeep, AutoBackupIncludeMedia) =
            (s.WarnFreeGb, s.LogKeepDays, s.BackupDir ?? "", s.AutoBackup, s.AutoBackupEveryHours, s.AutoBackupKeep, s.AutoBackupIncludeMedia);
        MinFreeMb = long.TryParse(await publish.GetAsync(SettingKeys.DiskMinFreeMb, CancellationToken.None), NumberStyles.None, CultureInfo.InvariantCulture, out var mb)
            ? (int)Math.Min(mb, int.MaxValue) : (int)DiskMonitor.DefaultMinFreeMb;
        RefreshBackups();
        await RefreshUsageAsync();
    }

    [RelayCommand]
    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in backups.List())
            Backups.Add(new BackupRow(b.Path, b.Name, b.Valid
                ? $"{Mb(b.SizeBytes)} · {b.CreatedUtc?.ToLocalTime():dd/MM/yyyy HH:mm} · {(b.IncludesMedia ? "kèm media" : "không media")}"
                : b.Error ?? "Không hợp lệ", b.Valid));
        PendingText = backups.HasPendingRestore ? "Đã hẹn khôi phục: sẽ áp khi bạn thoát hẳn app (menu khay > Thoát) rồi mở lại." : null;
    }

    [RelayCommand]
    private async Task RefreshUsageAsync()
    {
        Disks.Clear();
        foreach (var d in disks.Read())
            Disks.Add(new DiskRow($"{d.Root} ({d.Label})", $"trống {d.FreeBytes / (1024.0 * 1024 * 1024):0.0} / {d.TotalBytes / (1024.0 * 1024 * 1024):0.0} GB{(d.Low ? " — SẮP ĐẦY" : "")}"));
        try
        {
            var usage = await storage.MeasureAsync();
            Folders.Clear();
            foreach (var f in usage.OrderByDescending(f => f.Bytes))
                Folders.Add(new FolderRow(f.Name, Mb(f.Bytes), FolderNotes.GetValueOrDefault(f.Name, "")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Error($"Không đo được dung lượng: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task QuickBackupAsync() => EnqueueBackupAsync(null);

    [RelayCommand]
    private async Task BackupToAsync()
    {
        var path = dialogs.SaveFile("Lưu bản sao lưu", "Sao lưu SuperMOOO (*.zip)|*.zip", $"supermooo-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (path is not null) await EnqueueBackupAsync(path);
    }

    private async Task EnqueueBackupAsync(string? target)
    {
        try
        {
            var payload = new JsonObject { ["includeMedia"] = IncludeMedia, ["kind"] = "manual" };
            if (target is not null) payload["targetPath"] = target;
            await queue.EnqueueAsync(new JobRequest(BackupJobTypes.Create, "Sao lưu dữ liệu", payload, JobLane.Cloud, Owner: JobOwner.Screen("S16")));
            Status = "Đã xếp tác vụ sao lưu (xem khung Tác vụ bên dưới).";
        }
        catch (Exception ex)
        {
            dialogs.Error($"Không xếp được tác vụ sao lưu: {ex.Message}");
        }
    }

    [RelayCommand]
    private Task RestoreSelectedAsync() => SelectedBackup is { } row ? RestoreAsync(row.Path) : Task.CompletedTask;

    [RelayCommand]
    private async Task RestoreFromFileAsync()
    {
        var path = dialogs.OpenFile("Chọn file sao lưu", "Sao lưu SuperMOOO (*.zip)|*.zip");
        if (path is not null) await RestoreAsync(path);
    }

    private async Task RestoreAsync(string path)
    {
        var info = backups.Inspect(path);
        if (!info.Valid)
        {
            dialogs.Error(info.Error ?? "File sao lưu không hợp lệ.");
            return;
        }

        var message = $"Khôi phục từ \"{info.Name}\" (tạo {info.CreatedUtc?.ToLocalTime():dd/MM/yyyy HH:mm}, {info.Machine}\\{info.User})?\n\n"
                      + "Dữ liệu hiện tại được sao lưu trước. Bản khôi phục chỉ áp khi bạn thoát hẳn app rồi mở lại; dữ liệu cũ vẫn được giữ cạnh với hậu tố .before-restore."
                      + (info.Warnings.Count > 0 ? "\n\n" + string.Join("\n", info.Warnings.Select(w => "- " + w)) : "");
        if (!await dialogs.ConfirmAsync("Khôi phục dữ liệu", message, "Hẹn khôi phục")) return;

        try
        {
            await queue.EnqueueAsync(new JobRequest(BackupJobTypes.Stage, "Chuẩn bị khôi phục", new JsonObject { ["path"] = path }, JobLane.Cloud, Owner: JobOwner.Screen("S16")));
            Status = "Đang chuẩn bị khôi phục (xem khung Tác vụ bên dưới).";
        }
        catch (Exception ex)
        {
            dialogs.Error($"Không xếp được tác vụ khôi phục: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelPending()
    {
        backups.CancelPendingRestore();
        RefreshBackups();
        Status = "Đã huỷ khôi phục đang chờ.";
    }

    /// <summary>Gọi trên UI thread khi một job của màn này xong.</summary>
    public void OnJobSucceeded(JobSnapshot job)
    {
        if (job.Type == BackupJobTypes.Create)
        {
            dialogs.Info("Sao lưu", $"Đã tạo bản sao lưu: {job.Result?["path"]?.GetValue<string>()}");
            RefreshBackups();
        }
        else if (job.Type == BackupJobTypes.Stage)
        {
            dialogs.Info("Khôi phục", "Đã chuẩn bị xong. Thoát hẳn app (menu khay > Thoát) rồi mở lại để áp.");
            RefreshBackups();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await settings.SetAsync("storage", new StorageSettings
            {
                WarnFreeGb = Math.Max(0, WarnFreeGb), LogKeepDays = Math.Max(0, LogKeepDays), BackupDir = string.IsNullOrWhiteSpace(BackupDir) ? null : BackupDir.Trim(),
                AutoBackup = AutoBackup, AutoBackupEveryHours = Math.Max(1, AutoBackupEveryHours), AutoBackupKeep = Math.Max(1, AutoBackupKeep),
                AutoBackupIncludeMedia = AutoBackupIncludeMedia
            });
            await publish.SetAsync(SettingKeys.DiskMinFreeMb, Math.Max(0, MinFreeMb).ToString(CultureInfo.InvariantCulture), CancellationToken.None);
            Status = "Đã lưu cài đặt lưu trữ.";
            RefreshBackups();
            await RefreshUsageAsync();
        }
        catch (Exception ex)
        {
            dialogs.Error($"Không lưu được: {ex.Message}");
        }
    }

    [RelayCommand]
    private void BrowseBackupDir() => BackupDir = dialogs.PickFolder("Chọn thư mục chứa bản sao lưu") ?? BackupDir;

    [RelayCommand]
    private async Task CleanLogsAsync()
    {
        var result = await Task.Run(storage.CleanLogs);
        Status = $"Dọn log: xoá {result.FilesDeleted} file, giải phóng {Mb(result.BytesFreed)}.";
        await RefreshUsageAsync();
    }

    [RelayCommand]
    private async Task CleanCacheAsync()
    {
        if (!await dialogs.ConfirmAsync("Dọn cache", "Xoá file cache (trừ file đang dùng cho bài chờ đăng và tác vụ đang chạy)?", "Dọn")) return;
        try
        {
            var result = await cache.ClearAsync("all");
            Status = $"Dọn cache: xoá {result.FilesDeleted} file, giải phóng {Mb(result.BytesFreed)}.";
            await RefreshUsageAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            dialogs.Error($"Dọn cache lỗi: {ex.Message}");
        }
    }

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB" : $"{bytes / (1024.0 * 1024):0.#} MB";
}

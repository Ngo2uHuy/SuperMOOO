namespace SuperMOOO.Core.Models;

// Section "storage": sao lưu tự động, cảnh báo ổ đầy, giữ log. Ngưỡng tối thiểu để dời job dùng SettingKeys.DiskMinFreeMb (MB).
public sealed class StorageSettings
{
    public int WarnFreeGb { get; set; } = 5;          // dưới ngưỡng này: mục Cần xử lý + toast
    public int LogKeepDays { get; set; } = 14;
    public string? BackupDir { get; set; }            // rỗng = <DataRoot>\backups
    public bool AutoBackup { get; set; }
    public int AutoBackupEveryHours { get; set; } = 24;
    public int AutoBackupKeep { get; set; } = 7;
    public bool AutoBackupIncludeMedia { get; set; }
}

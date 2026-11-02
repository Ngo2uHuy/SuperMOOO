using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.Data.Legacy;

namespace SuperMOOO.App.Features.S16;

[Service(ServiceLifetime.Transient)]
public sealed partial class LegacyImportViewModel(LegacyImporter importer, IDialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    public partial string CrawFolder { get; set; } = "";

    [ObservableProperty]
    public partial string OldAppFolder { get; set; } = "";

    [ObservableProperty]
    public partial string ReportText { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private void BrowseCraw() => CrawFolder = dialogs.PickFolder("Chọn thư mục Craw (chứa streamgrabber.db)") ?? CrawFolder;

    [RelayCommand]
    private void BrowseOld() => OldAppFolder = dialogs.PickFolder("Chọn thư mục bản cũ (tuỳ chọn)") ?? OldAppFolder;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (string.IsNullOrWhiteSpace(CrawFolder)) { dialogs.Error("Chọn thư mục Craw trước."); return; }
        if (!await dialogs.ConfirmAsync("Nhập dữ liệu cũ", "Nhập dữ liệu từ thư mục đã chọn? Thư mục nguồn chỉ được đọc, không bị sửa.")) return;

        IsBusy = true;
        try
        {
            var report = await importer.ImportAsync(new LegacyImportOptions(CrawFolder.Trim(), string.IsNullOrWhiteSpace(OldAppFolder) ? null : OldAppFolder.Trim()));
            ReportText = Describe(report);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            dialogs.Error(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => !IsBusy;

    private static string Describe(ImportReport report)
    {
        var lines = new List<string> { $"Đã nhập {report.TotalImported} dòng." };
        lines.AddRange(report.Tables.Select(t => $"  {t.Key}: nhập {t.Value.Imported}/{t.Value.Read} (bỏ {t.Value.Skipped})"));
        if (report.InvalidKeys.Count > 0) lines.Add("Key sai định dạng: " + string.Join("; ", report.InvalidKeys));
        if (report.MissingFiles.Count > 0) lines.Add($"File không còn trên đĩa: {report.MissingFiles.Count}");
        lines.AddRange(report.Warnings.Select(w => "Cảnh báo: " + w));
        if (report.SavedPath is not null) lines.Add("Báo cáo JSON: " + report.SavedPath);
        return string.Join(Environment.NewLine, lines);
    }
}

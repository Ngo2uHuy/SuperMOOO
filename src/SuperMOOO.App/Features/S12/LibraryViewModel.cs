using System.IO;
using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S09;
using SuperMOOO.Data.Repositories.Library;
using SuperMOOO.Infrastructure.Shell;
using SuperMOOO.Social.Download;
using SuperMOOO.Social.Library;
using SuperMOOO.Social.Platforms;

namespace SuperMOOO.App.Features.S12;

public sealed record LibraryRowView(LibraryEntry Entry)
{
    public long Id => Entry.Item.Id;
    public string Platform => Entry.Item.Platform;
    public string Title => Entry.Item.Title ?? Entry.Item.Url ?? "";
    public string Author => Entry.Item.AuthorName ?? "";
    public string Project => Entry.Item.Project ?? "";
    public string Campaign => Entry.Item.Campaign ?? "";
    public string Tags => string.Join(", ", Entry.Item.Tags);
    public string Favorite => Entry.Item.Favorite ? "★" : "";
    public string File => Entry.Item.LocalPath is null ? "" : Entry.FileMissing ? "Thiếu file" : "Có file";
}

[Service(ServiceLifetime.Transient)]
public sealed partial class LibraryViewModel(LibraryService library, MediaLibraryService media, IDialogService dialogs, ShellService shell)
    : ObservableObject
{
    public IReadOnlyList<string> Platforms { get; } = ["all", .. PlatformRegistry.All.Where(p => p.Domains.Count > 0).Select(p => p.Id)];
    public ObservableCollection<LibraryRowView> Items { get; } = [];
    public ObservableCollection<HistoryRow> History { get; } = [];

    [ObservableProperty]
    public partial string SelectedPlatform { get; set; } = "all";

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    [ObservableProperty]
    public partial bool FavoriteOnly { get; set; }

    [ObservableProperty]
    public partial LibraryRowView? Selected { get; set; }

    [ObservableProperty]
    public partial string EditProject { get; set; } = "";

    [ObservableProperty]
    public partial string EditCampaign { get; set; } = "";

    [ObservableProperty]
    public partial string EditTags { get; set; } = "";

    [ObservableProperty]
    public partial bool EditFavorite { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnSelectedChanged(LibraryRowView? value)
    {
        EditProject = value?.Project ?? "";
        EditCampaign = value?.Campaign ?? "";
        EditTags = value?.Tags ?? "";
        EditFavorite = value?.Entry.Item.Favorite ?? false;
    }

    public async Task LoadAsync()
    {
        await SearchAsync();
        await RefreshHistoryAsync();
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        var entries = await library.QueryAsync(new LibraryFilter(SelectedPlatform, Text: SearchText, Favorite: FavoriteOnly ? true : null, Limit: 500));
        Show(entries);
        Status = $"{entries.Count} mục.";
    }

    [RelayCommand]
    private async Task FindMissingAsync()
    {
        var entries = await library.FindMissingFilesAsync();
        Show(entries);
        Status = $"{entries.Count} mục thiếu file.";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is not { } row) return;
        var tags = EditTags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        // Chuỗi rỗng không xoá được (null = giữ nguyên) nên chỉ gửi khi có nội dung; xoá nhãn dùng danh sách rỗng.
        await library.UpdateAsync(row.Id, new LibraryPatch(NullIfEmpty(EditProject), NullIfEmpty(EditCampaign), EditFavorite, tags));
        await SearchAsync();
        Status = "Đã lưu.";
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { } row || !await dialogs.ConfirmAsync("Xoá mục thư viện", $"Xoá \"{row.Title}\" khỏi thư viện? File trên ổ đĩa được giữ.")) return;
        await library.DeleteAsync([row.Id]);
        await SearchAsync();
    }

    [RelayCommand]
    private void OpenFile()
    {
        try
        {
            if (Selected?.Entry.Item.LocalPath is { } path) shell.OpenFile(path);
        }
        catch (IOException ex)
        {
            dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        var rows = await media.HistoryAsync();
        History.Clear();
        foreach (var row in rows) History.Add(new HistoryRow(row));
    }

    private void Show(IReadOnlyList<LibraryEntry> entries)
    {
        Items.Clear();
        foreach (var entry in entries) Items.Add(new LibraryRowView(entry));
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

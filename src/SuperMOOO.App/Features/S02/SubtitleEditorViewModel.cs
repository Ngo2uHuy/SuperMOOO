using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.AI.Catalog;
using SuperMOOO.App.Controls;
using SuperMOOO.App.Features.S01;
using SuperMOOO.Workflows;
using SuperMOOO.Workflows.Localize;
using SuperMOOO.Workflows.Subtitles;
using SuperMOOO.Core.Jobs;

namespace SuperMOOO.App.Features.S02;

public sealed record SubtitleRow(int Number, SubtitleEntry Entry)
{
    public string Start => SubtitleEditing.FormatTime(Entry.Start);
    public string End => SubtitleEditing.FormatTime(Entry.End);
    public string Duration => $"{Math.Max(0, Entry.End - Entry.Start):0.00}s";
    public string Text => Entry.Text;
    public string Translation => Entry.Translation;
}

/// <summary>
/// Danh sách + trình sửa từng câu dùng chung cho S02 và S06 (R15). Dịch/ASR/áp dụng vào dự án chỉ có ở S02.
/// </summary>
[Service(ServiceLifetime.Transient)]
public sealed partial class SubtitleEditorViewModel(SubtitleEditingService editing, LocalizeService localize, IAudioPlayer player,
    JobTracker tracker, IDialogService dialogs) : JobViewModelBase(tracker, dialogs)
{
    /// <summary>Màn đang nhúng trình sửa (S06 dùng lại) để job bóc phụ đề hiện ở khung tác vụ của đúng màn đó.</summary>
    public string JobsScreen { get; set; } = "S02";

    protected override string JobsOwner => JobOwner.Screen(JobsScreen);

    private IReadOnlyList<SubtitleEntry> _entries = [];

    public ObservableCollection<SubtitleRow> Rows { get; } = [];
    public IReadOnlyList<LanguageChoice> Languages { get; } = [.. LanguageCatalog.All.Select(l => new LanguageChoice(l.Code, $"{l.Name} ({l.Code})"))];

    /// <summary>S06 chỉ sửa một lớp chữ, nên ẩn cột/ô bản dịch.</summary>
    [ObservableProperty] public partial bool ShowTranslation { get; set; } = true;
    [ObservableProperty] public partial string SourceVideo { get; set; } = "";
    [ObservableProperty] public partial LanguageChoice? TargetLanguage { get; set; }
    [ObservableProperty] public partial SubtitleRow? SelectedRow { get; set; }
    [ObservableProperty] public partial string EditStart { get; set; } = "";
    [ObservableProperty] public partial string EditEnd { get; set; } = "";
    [ObservableProperty] public partial string EditText { get; set; } = "";
    [ObservableProperty] public partial string EditTranslation { get; set; } = "";
    [ObservableProperty] public partial string CountText { get; set; } = "Tổng số câu: 0";

    public IReadOnlyList<SubtitleEntry> Entries => _entries;

    public void Initialize() => TargetLanguage ??= Languages[0];

    public void Load(IReadOnlyList<SubtitleEntry> entries)
    {
        _entries = entries;
        Refresh(-1);
    }

    partial void OnSelectedRowChanged(SubtitleRow? value)
    {
        if (value is null) return;
        EditStart = value.Start;
        EditEnd = value.End;
        EditText = value.Entry.Text;
        EditTranslation = value.Entry.Translation;
    }

    private void Refresh(int select)
    {
        Rows.Clear();
        for (var i = 0; i < _entries.Count; i++) Rows.Add(new SubtitleRow(i + 1, _entries[i]));
        CountText = $"Tổng số câu: {_entries.Count}";
        SelectedRow = select >= 0 && select < Rows.Count ? Rows[select] : null;
    }

    /// <summary>Chỉ thay đúng dòng vừa lưu để không dựng lại cả danh sách (mất cuộn/lựa chọn).</summary>
    private void RefreshRow(int index)
    {
        Rows[index] = new SubtitleRow(index + 1, _entries[index]);
        SelectedRow = Rows[index];
    }

    private int SelectedIndex => SelectedRow is null ? -1 : SelectedRow.Number - 1;

    [RelayCommand]
    private Task ImportSrtAsync() => GuardAsync(async () =>
    {
        if (Dialogs.OpenFile("Chọn file phụ đề SRT", "SRT (*.srt)|*.srt") is not { } file) return;
        Load(await editing.OpenSrtAsync(file));
        Status = $"Đã nhập {_entries.Count} câu từ {Path.GetFileName(file)}.";
    });

    [RelayCommand]
    private Task ExportSrtAsync() => GuardAsync(async () =>
    {
        if (_entries.Count == 0) throw new InvalidOperationException("Chưa có phụ đề nào để xuất.");
        if (Dialogs.SaveFile("Lưu file SRT", "SRT (*.srt)|*.srt", "subtitles.srt") is not { } file) return;
        await editing.SaveSrtAsync(_entries, file, useTranslation: ShowTranslation);
        Status = $"Đã lưu: {file}";
    });

    [RelayCommand]
    private void AddLine()
    {
        _entries = SubtitleEditing.Add(_entries);
        Refresh(_entries.Count - 1);
    }

    [RelayCommand]
    private void DeleteLine()
    {
        if (SelectedIndex < 0) return;
        _entries = SubtitleEditing.Remove(_entries, SelectedIndex);
        Refresh(-1);
        EditStart = EditEnd = EditText = EditTranslation = "";
    }

    [RelayCommand]
    private void SaveLine()
    {
        var index = SelectedIndex;
        if (index < 0)
        {
            Dialogs.Info("Chưa chọn câu", "Chọn câu cần sửa trong danh sách trước.");
            return;
        }

        try
        {
            var entry = _entries[index] with
            {
                Start = SubtitleEditing.ParseTime(EditStart), End = SubtitleEditing.ParseTime(EditEnd),
                Text = EditText.Trim(), Translation = EditTranslation.Trim()
            };
            _entries = SubtitleEditing.Update(_entries, index, entry);
            RefreshRow(index);
            Status = "Đã cập nhật câu.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            Dialogs.Error(ex.Message);
        }
    }

    [RelayCommand]
    private void BrowseSource()
    {
        if (Dialogs.OpenFile("Chọn video nguồn", "Media|*.mp4;*.mkv;*.mov;*.avi;*.flv;*.ts;*.webm;*.mp3;*.wav|Tất cả|*.*") is { } file) SourceVideo = file;
    }

    /// <summary>Bóc phụ đề gốc bằng ASR (pipeline `subtitles`, không dịch) rồi nạp vào bảng.</summary>
    [RelayCommand]
    private async Task TranscribeAsync()
    {
        if (!File.Exists(SourceVideo)) { Dialogs.Error("Chọn video nguồn để bóc phụ đề."); return; }
        var options = new LocalizeOptions { Translate = false, BurnSubtitles = false, UseEditedSubtitles = false, TargetLanguage = TargetLanguage?.Code ?? "vi" };
        var job = await RunJobAsync(ct => localize.StartSubtitlesAsync(SourceVideo, options, ct), "Đang bóc phụ đề…");
        if (job is null || PipelineResult.Value(job.Result, "segmentsFile") is not { } file) return;
        await GuardAsync(async () =>
        {
            Load(await SubtitleEntries.ReadAsync(file));
            Status = $"Đã bóc {_entries.Count} câu.";
        });
    }

    [RelayCommand]
    private Task TranslateLineAsync() => GuardAsync(async () =>
    {
        var row = SelectedRow;
        var original = EditText;
        var translated = await editing.TranslateLineAsync(original, TargetLanguage?.Code ?? "vi");
        // Kết quả đến muộn chỉ áp nếu người dùng vẫn đang ở đúng câu đó.
        if (SelectedRow == row && EditText == original) EditTranslation = translated;
    });

    [RelayCommand]
    private async Task TranslateAllAsync()
    {
        if (_entries.Count == 0) return;
        await RunDirectAsync(async (_, ct) =>
        {
            var keep = SelectedIndex;
            _entries = await editing.TranslateAllAsync(_entries, "auto", TargetLanguage?.Code ?? "vi", ct: ct);
            Refresh(keep);
            Status = $"Đã dịch {_entries.Count} câu.";
        }, "Đang dịch cả file…");
    }

    [RelayCommand]
    private Task PreviewLineAsync(CancellationToken ct) => GuardAsync(async () =>
    {
        if (SelectedRow is not { } row) throw new InvalidOperationException("Chọn câu muốn nghe thử.");
        var entry = row.Entry with { Text = EditText, Translation = EditTranslation };
        player.Play(await editing.PreviewLineAsync(entry, TargetLanguage?.Code ?? "vi", ct));
    });

    /// <summary>Dừng cả khi giọng còn đang tạo: nút loa bị khoá trong lúc chờ nên nút dừng phải huỷ được lượt đó.</summary>
    [RelayCommand]
    private void StopAudio()
    {
        PreviewLineCommand.Cancel();
        player.Stop();
    }

    [RelayCommand]
    private Task ApplyToProjectAsync() => GuardAsync(async () =>
    {
        if (!File.Exists(SourceVideo)) throw new InvalidOperationException("Chọn video nguồn của dự án lồng tiếng trước.");
        await editing.ApplyToProjectAsync(SourceVideo, _entries);
        Dialogs.Info("Đã áp dụng", $"Đã lưu {_entries.Count} câu vào dự án của {Path.GetFileName(SourceVideo)}; S01 sẽ dùng thay cho ASR.");
    });

    /// <summary>Mở lại phụ đề đã lưu cho video nguồn (nếu có).</summary>
    [RelayCommand]
    private Task LoadSavedAsync() => GuardAsync(async () =>
    {
        if (!File.Exists(SourceVideo)) throw new InvalidOperationException("Chọn video nguồn trước.");
        Load(await localize.LoadSavedSubtitlesAsync(SourceVideo) ?? throw new InvalidOperationException("Video này chưa có phụ đề đã lưu."));
    });
}

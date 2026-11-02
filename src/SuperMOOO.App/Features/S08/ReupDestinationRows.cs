using SuperMOOO.App.Controls;
using SuperMOOO.Workflows.AutoReup;

namespace SuperMOOO.App.Features.S08;

/// <summary>Một kênh đích trong bảng của luồng: bật/tắt, hồ sơ thị trường, ghi đè riêng.</summary>
public sealed partial class ReupDestinationRow(DestinationOption option, Action<ReupDestinationRow> changed) : ObservableObject
{
    /// <summary>Giá trị ComboBox hồ sơ ứng với MarketProfileId = null (theo hồ sơ của tài khoản).</summary>
    public const string ByAccount = "account";

    public DestinationOption Option { get; } = option;
    public string Id => Option.Id;
    public string ConnectionId => Option.Destination.ConnectionId;
    public string Label => Option.Label;
    public ReupOverrides Overrides { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileChoice))]
    public partial string ProfileId { get; set; } = ReupDestinationOptions.NoProfile;

    /// <summary>Cho ComboBox: bỏ qua null mà binding ghi về khi danh sách lựa chọn bị nạp lại.</summary>
    public string? ProfileChoice
    {
        get => ProfileId;
        set { if (value is not null) ProfileId = value; }
    }

    [ObservableProperty]
    public partial string Schedule { get; set; } = "";

    [ObservableProperty]
    public partial bool HasOverrides { get; set; }

    /// <summary>Cảnh báo khi hồ sơ đã chọn không còn (engine vẫn chạy bằng cài đặt chung).</summary>
    [ObservableProperty]
    public partial string ProfileWarning { get; set; } = "";

    partial void OnProfileIdChanged(string value) => changed(this);

    public ReupDestinationOptions ToOptions() => new()
    {
        DestinationId = Id, Enabled = Option.IsSelected, MarketProfileId = ProfileId == ByAccount ? null : ProfileId, Overrides = Overrides
    };
}

/// <summary>Một trường ghi đè trong vùng "Tuỳ chỉnh": hiện giá trị kế thừa, sửa thì thành ghi đè (chữ đậm), "Về như hồ sơ" bỏ ghi đè.</summary>
public sealed partial class OverrideField(string key, string header, IReadOnlyList<ReupChoice>? options = null, bool multiLine = false, bool days = false) : ObservableObject
{
    private string _inherited = "";
    private bool _syncing;

    public string Key { get; } = key;
    public string Header { get; } = header;
    public IReadOnlyList<ReupChoice>? Options { get; } = options;
    public bool IsChoice => Options is not null;
    public bool IsDays { get; } = days;
    public bool IsText => Options is null && !IsDays;
    public bool MultiLine { get; } = multiLine;

    /// <summary>7 ô chọn T2…CN; <see cref="Value"/> của trường ngày là số DayOfWeek cách nhau dấu phẩy, rỗng = mọi ngày.</summary>
    public IReadOnlyList<DayToggle> DayToggles => _toggles ??= !IsDays ? [] :
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayToggle(d, OnToggled))];

    private IReadOnlyList<DayToggle>? _toggles;

    private void SyncToggles()
    {
        var chosen = Value.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var t in DayToggles) t.IsChecked = chosen.Count == 0 || chosen.Contains(((int)t.Day).ToString());
    }

    private void OnToggled()
    {
        if (_syncing) return;
        Value = string.Join(",", DayToggles.Where(t => t.IsChecked).Select(t => (int)t.Day));
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoiceValue))]
    public partial string Value { get; set; } = "";

    /// <summary>Cho ComboBox: bỏ qua null mà binding ghi về khi giá trị không có trong danh sách.</summary>
    public string? ChoiceValue
    {
        get => Value;
        set { if (value is not null && IsChoice) Value = value; }
    }

    [ObservableProperty]
    public partial bool IsSet { get; set; }

    partial void OnValueChanged(string value)
    {
        if (!_syncing) IsSet = true;
    }

    public void Load(string? own, string inherited)
    {
        _inherited = inherited;
        _syncing = true;
        Value = own ?? inherited;
        IsSet = own is not null;
        if (IsDays) SyncToggles();
        _syncing = false;
    }

    public string? Own => IsSet ? Value ?? "" : null;

    [RelayCommand]
    private void Reset()
    {
        _syncing = true;
        Value = _inherited;
        IsSet = false;
        if (IsDays) SyncToggles();
        _syncing = false;
    }
}

/// <summary>Một ô chọn thứ trong tuần của trường "Ngày được đăng".</summary>
public sealed partial class DayToggle(DayOfWeek day, Action changed) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Label => ReupMarketService.DayName(Day);

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}

/// <summary>Kết quả "Xem trước" của một kênh.</summary>
public sealed record ReupPreviewRow(string Channel, string Profile, string Title, string Caption, string DueLocal, string DueMachine, string Warning);

using System.Collections.ObjectModel;
using System.IO;
using SuperMOOO.App.Controls;
using SuperMOOO.Media.Subtitles;

namespace SuperMOOO.App.Features.S18;

public sealed record BrandingColorPreset(string Name, string Text, string Outline, int OutlineWidth, bool Box);

/// <summary>Tab "Thương hiệu" của S18: soạn một mẫu phụ đề + watermark/logo, lưu nhiều mẫu đặt tên vào settings "branding". Job lấy bản chụp của mẫu đang soạn lúc bấm Bắt đầu.</summary>
public sealed partial class BrandingEditorModel(ISettingsStore settings, IDialogService dialogs, SubtitleStylePresets stylePresets) : ObservableObject
{
    private bool _loading;

    public static IReadOnlyList<WhiteboardChoice> SubtitleModeChoices { get; } =
    [
        new(SubtitleModes.Burn, "Đốt vào hình"), new(SubtitleModes.Srt, "Chỉ file .srt rời"),
        new(SubtitleModes.Both, "Cả hai (đốt + .srt)"), new(SubtitleModes.None, "Tắt phụ đề")
    ];

    public static IReadOnlyList<WhiteboardChoice> Positions { get; } =
        [new("TL", "Góc trên trái"), new("TR", "Góc trên phải"), new("BL", "Góc dưới trái"), new("BR", "Góc dưới phải"), new("C", "Giữa khung")];

    // Cùng bộ mẫu màu nhanh của VideoC (kiểu YouTube/TikTok/Facebook).
    public static IReadOnlyList<BrandingColorPreset> ColorPresets { get; } =
    [
        new("Trắng viền đen", "#FFFFFF", "#000000", 6, false), new("Vàng TikTok", "#FFE600", "#000000", 7, false),
        new("Cyan Neon", "#00F0FF", "#002A33", 6, false), new("Khung nền đen", "#FFFFFF", "#000000", 0, true),
        new("Xanh Facebook", "#FFFFFF", "#1877F2", 7, false), new("Đỏ nổi bật", "#FFFFFF", "#D50000", 7, false)
    ];

    public IReadOnlyList<string> Fonts { get; } = [.. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];
    public IReadOnlyList<SubtitleStylePreset> StylePresets => stylePresets.All;
    public ObservableCollection<string> PresetNames { get; } = [];

    [ObservableProperty] public partial string? SelectedPreset { get; set; }
    [ObservableProperty] public partial string PresetName { get; set; } = "Mặc định";
    [ObservableProperty] public partial SubtitleStylePreset? SelectedStylePreset { get; set; }
    [ObservableProperty] public partial string SampleText { get; set; } = "Chỉ để cảm thấy mình thông minh.";

    [ObservableProperty] public partial string SubtitleMode { get; set; } = SubtitleModes.Burn;
    [ObservableProperty] public partial string FontName { get; set; } = "Segoe UI";
    [ObservableProperty] public partial double FontSize { get; set; } = 54;
    [ObservableProperty] public partial string TextColor { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial string OutlineColor { get; set; } = "#000000";
    [ObservableProperty] public partial int Outline { get; set; } = 6;
    [ObservableProperty] public partial double MaxChars { get; set; } = 42;
    [ObservableProperty] public partial bool UpperCase { get; set; }
    [ObservableProperty] public partial bool Bold { get; set; } = true;
    [ObservableProperty] public partial bool BoxBackground { get; set; }
    [ObservableProperty] public partial string BackColor { get; set; } = "#000000";
    [ObservableProperty] public partial double BackOpacity { get; set; } = 0.65;
    [ObservableProperty] public partial double SubtitleY { get; set; } = 86;
    [ObservableProperty] public partial double SubtitleLeft { get; set; } = 5;
    [ObservableProperty] public partial double SubtitleWidth { get; set; } = 90;

    [ObservableProperty] public partial bool WatermarkEnabled { get; set; } = true;
    [ObservableProperty] public partial string WatermarkText { get; set; } = "";
    [ObservableProperty] public partial string WatermarkFont { get; set; } = "Segoe UI";
    [ObservableProperty] public partial int WatermarkSize { get; set; } = 40;
    [ObservableProperty] public partial string WatermarkColor { get; set; } = "#FFFFFF";
    [ObservableProperty] public partial string WatermarkOutline { get; set; } = "#000000";
    [ObservableProperty] public partial double WatermarkOpacity { get; set; } = 0.85;
    [ObservableProperty] public partial string WatermarkPosition { get; set; } = "TL";
    // Toạ độ tự do (% góc trên trái) do kéo thả; NaN = dùng mã góc (preset cũ).
    [ObservableProperty] public partial double WatermarkX { get; set; } = double.NaN;
    [ObservableProperty] public partial double WatermarkY { get; set; } = double.NaN;

    [ObservableProperty] public partial string LogoPath { get; set; } = "";
    [ObservableProperty] public partial string LogoPosition { get; set; } = "TR";
    [ObservableProperty] public partial double LogoX { get; set; } = double.NaN;
    [ObservableProperty] public partial double LogoY { get; set; } = double.NaN;
    [ObservableProperty] public partial double LogoOpacity { get; set; } = 0.9;
    [ObservableProperty] public partial double LogoSizePercent { get; set; } = 12;

    public bool ShowsSubtitleGuide => SubtitleMode is SubtitleModes.Burn or SubtitleModes.Both;

    partial void OnSubtitleModeChanged(string value) => OnPropertyChanged(nameof(ShowsSubtitleGuide));

    // Chọn lại góc thì bỏ vị trí tự do để góc có hiệu lực.
    partial void OnWatermarkPositionChanged(string value) { if (!_loading) (WatermarkX, WatermarkY) = (double.NaN, double.NaN); }

    partial void OnLogoPositionChanged(string value) { if (!_loading) (LogoX, LogoY) = (double.NaN, double.NaN); }

    public bool HasWatermark => WatermarkText.Trim().Length > 0;

    public bool HasLogo => LogoPath.Trim().Length > 0 && File.Exists(LogoPath.Trim());

    partial void OnWatermarkTextChanged(string value) => OnPropertyChanged(nameof(HasWatermark));

    partial void OnLogoPathChanged(string value) => OnPropertyChanged(nameof(HasLogo));

    partial void OnSelectedPresetChanged(string? value)
    {
        if (_loading || value is null) return;
        if (settings.Get<BrandingSettings>("branding").Presets.FirstOrDefault(p => p.Name == value) is { } preset) Apply(preset);
    }

    public void Load()
    {
        var saved = settings.Get<BrandingSettings>("branding");
        if (saved.Presets.Count == 0) saved.Presets.Add(new BrandingPreset());
        Refresh(saved, saved.Presets.FirstOrDefault(p => p.Name == saved.Active)?.Name ?? saved.Presets[0].Name);
        Apply(saved.Presets.First(p => p.Name == SelectedPreset));
        SelectedStylePreset ??= StylePresets.FirstOrDefault();
    }

    private void Refresh(BrandingSettings saved, string selected)
    {
        _loading = true;
        PresetNames.Clear();
        foreach (var p in saved.Presets) PresetNames.Add(p.Name);
        SelectedPreset = selected;
        _loading = false;
    }

    private void Apply(BrandingPreset p)
    {
        _loading = true;
        PresetName = p.Name;
        SubtitleMode = p.SubtitleMode;
        var s = p.Subtitle;
        (FontName, FontSize, TextColor, OutlineColor, Outline, Bold) = (s.FontName, s.FontSize, s.PrimaryColor, s.OutlineColor, s.Outline, s.Bold);
        (MaxChars, UpperCase, WatermarkEnabled) = (Math.Clamp(p.SubtitleMaxChars, 16, 60), p.SubtitleUpperCase, p.WatermarkEnabled);
        (BoxBackground, BackColor, BackOpacity) = (s.BoxBackground, s.BackColor, s.BackOpacity);
        (SubtitleY, SubtitleLeft, SubtitleWidth) = (s.PositionY ?? 86, s.BoxLeft ?? 5, s.BoxWidth ?? 90);
        (WatermarkText, WatermarkFont, WatermarkSize, WatermarkColor, WatermarkOutline) = (p.WatermarkText, p.WatermarkFont, p.WatermarkSize, p.WatermarkColor, p.WatermarkOutline);
        (WatermarkOpacity, WatermarkPosition) = (p.WatermarkOpacity, p.WatermarkPosition);
        (LogoPath, LogoPosition, LogoOpacity, LogoSizePercent) = (p.LogoPath ?? "", p.LogoPosition, p.LogoOpacity, p.LogoSizePercent);
        (WatermarkX, WatermarkY, LogoX, LogoY) = (p.WatermarkX ?? double.NaN, p.WatermarkY ?? double.NaN, p.LogoX ?? double.NaN, p.LogoY ?? double.NaN);
        _loading = false;
    }

    private static double? Free(double v) => double.IsFinite(v) ? v : null;

    public BrandingPreset Build() => new()
    {
        Name = PresetName.Trim().Length > 0 ? PresetName.Trim() : "Mặc định",
        SubtitleMode = SubtitleMode,
        Subtitle = new SubtitleStyle(FontName, (int)Math.Round(FontSize), TextColor, OutlineColor, Outline, 40, "bottom", BackColor, BackOpacity, BoxBackground, Bold,
            SubtitleY, null, SubtitleLeft, SubtitleWidth),
        SubtitleMaxChars = (int)Math.Round(MaxChars), SubtitleUpperCase = UpperCase, WatermarkEnabled = WatermarkEnabled,
        WatermarkText = WatermarkText, WatermarkFont = WatermarkFont, WatermarkSize = WatermarkSize, WatermarkColor = WatermarkColor,
        WatermarkOutline = WatermarkOutline, WatermarkOpacity = WatermarkOpacity, WatermarkPosition = WatermarkPosition,
        WatermarkX = Free(WatermarkX), WatermarkY = Free(WatermarkY), LogoX = Free(LogoX), LogoY = Free(LogoY),
        LogoPath = LogoPath.Trim().Length > 0 ? LogoPath.Trim() : null, LogoPosition = LogoPosition, LogoOpacity = LogoOpacity, LogoSizePercent = LogoSizePercent
    };

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        var preset = Build();
        var saved = settings.Get<BrandingSettings>("branding");
        var index = saved.Presets.FindIndex(p => p.Name == preset.Name);
        if (index >= 0) saved.Presets[index] = preset;
        else saved.Presets.Add(preset);
        saved.Active = preset.Name;
        await settings.SetAsync("branding", saved);
        Refresh(saved, preset.Name);
        dialogs.Info("Thương hiệu", $"Đã lưu mẫu \"{preset.Name}\".");
    }

    [RelayCommand]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is not { } name) return;
        var saved = settings.Get<BrandingSettings>("branding");
        saved.Presets.RemoveAll(p => p.Name == name);
        if (saved.Presets.Count == 0) saved.Presets.Add(new BrandingPreset());
        saved.Active = saved.Presets[0].Name;
        await settings.SetAsync("branding", saved);
        Refresh(saved, saved.Active);
        Apply(saved.Presets[0]);
    }

    [RelayCommand]
    private void ApplyColorPreset(BrandingColorPreset preset)
    {
        (TextColor, OutlineColor, Outline, BoxBackground) = (preset.Text, preset.Outline, preset.OutlineWidth, preset.Box);
    }

    [RelayCommand]
    private void ApplyStylePreset()
    {
        if (SelectedStylePreset is not { Style: var s }) return;
        (FontName, TextColor, BackColor, BoxBackground) = (s.FontName, s.PrimaryColor, s.BackColor, s.BoxBackground);
    }

    [RelayCommand]
    private void PickLogo()
    {
        if (dialogs.OpenFile("Chọn ảnh logo", "Ảnh logo (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp") is { } file && File.Exists(file)) LogoPath = file;
    }

    [RelayCommand]
    private void ClearLogo() => (LogoPath, LogoX, LogoY) = ("", double.NaN, double.NaN);

    [RelayCommand]
    private void ResetOverlayPositions() => (WatermarkX, WatermarkY, LogoX, LogoY) = (double.NaN, double.NaN, double.NaN, double.NaN);

    [RelayCommand]
    private void CenterSubtitle() => (SubtitleLeft, SubtitleWidth) = (5, 90);
}

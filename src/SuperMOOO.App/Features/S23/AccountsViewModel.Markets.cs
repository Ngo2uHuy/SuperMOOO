using System.Collections.ObjectModel;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.App.Features.S23;

public sealed partial class DayChoice(DayOfWeek day, string name) : ObservableObject
{
    public DayOfWeek Day { get; } = day;
    public string Name { get; } = name;
    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>Hồ sơ thị trường (lưu ở section "publish.markets") và gắn hồ sơ cho tài khoản.</summary>
public sealed partial class AccountsViewModel
{
    private bool _marketLoading;

    public ObservableCollection<MarketProfile> Markets { get; } = [];
    /// <summary>Lựa chọn hồ sơ cho tài khoản; "" = không gắn.</summary>
    public ObservableCollection<Opt> MarketOptions { get; } = [];
    public IReadOnlyList<Opt> LanguageOptions { get; } = [new("", "(Không đặt)"), .. LanguageCatalog.All.Select(l => new Opt(l.Code, $"{l.Name} ({l.Code})"))];
    public IReadOnlyList<Opt> TimeZoneOptions { get; } =
        [new("", "Giờ của máy này"), .. TimeZoneInfo.GetSystemTimeZones().Select(z => new Opt(z.Id, z.DisplayName))];
    public IReadOnlyList<string> PrivacyOptions { get; } = ["private", "unlisted", "public"];
    public ObservableCollection<DayChoice> MarketDays { get; } =
        [.. new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
            .Select(d => new DayChoice(d, d == DayOfWeek.Sunday ? "CN" : "T" + ((int)d + 1)) { IsChecked = true })];

    [ObservableProperty]
    public partial MarketProfile? SelectedMarket { get; set; }
    [ObservableProperty]
    public partial string MarketName { get; set; } = "";
    [ObservableProperty]
    public partial string MarketCountry { get; set; } = "";
    [ObservableProperty]
    public partial string MarketLanguage { get; set; } = "";
    [ObservableProperty]
    public partial string MarketTimeZone { get; set; } = "";
    [ObservableProperty]
    public partial string MarketWindows { get; set; } = "";
    [ObservableProperty]
    public partial int MarketDailyLimit { get; set; } = 20;
    [ObservableProperty]
    public partial bool MarketTranslate { get; set; }
    [ObservableProperty]
    public partial string MarketHashtags { get; set; } = "";
    [ObservableProperty]
    public partial string MarketTitleTemplate { get; set; } = "{title}";
    [ObservableProperty]
    public partial string MarketCaptionTemplate { get; set; } = "{title}\n{hashtags}";
    [ObservableProperty]
    public partial string MarketPrivacy { get; set; } = "private";

    // --- Hồ sơ của tài khoản đang xem ---
    [ObservableProperty]
    public partial string AccountMarketId { get; set; } = "";
    [ObservableProperty]
    public partial string? MarketWarning { get; set; }

    partial void OnSelectedMarketChanged(MarketProfile? value)
    {
        if (value is null) return;
        MarketName = value.Name;
        MarketCountry = value.Country;
        MarketLanguage = value.Language;
        MarketTimeZone = value.TimeZoneId;
        MarketWindows = MarketProfile.FormatWindows(value.Windows);
        foreach (var d in MarketDays) d.IsChecked = value.Days.Count == 0 || value.Days.Contains(d.Day);
        MarketDailyLimit = value.DailyLimit;
        MarketTranslate = value.TranslateText;
        MarketHashtags = value.Hashtags;
        MarketTitleTemplate = value.TitleTemplate;
        MarketCaptionTemplate = value.CaptionTemplate;
        MarketPrivacy = value.Privacy;
    }

    partial void OnAccountMarketIdChanged(string value)
    {
        if (_marketLoading || SelectedAccount is not { } a) return;
        _ = Guard(async () =>
        {
            var s = _settings.Get<MarketSettings>(MarketSettings.Section);
            if (string.IsNullOrEmpty(value)) s.Accounts.Remove(a.Id);
            else s.Accounts[a.Id] = value;
            await _settings.SetAsync(MarketSettings.Section, s);
            await RefreshMarketWarningAsync(a.Id);
            Status = string.IsNullOrEmpty(value) ? $"Đã bỏ hồ sơ thị trường của \"{a.Name}\"." : $"Đã gắn hồ sơ thị trường cho \"{a.Name}\".";
        });
    }

    private void ReloadMarkets()
    {
        var s = _settings.Get<MarketSettings>(MarketSettings.Section);
        var keep = SelectedMarket?.Id;
        Markets.Clear();
        foreach (var p in s.Profiles) Markets.Add(p);
        _marketLoading = true;
        try
        {
            var current = AccountMarketId;
            MarketOptions.Clear();
            MarketOptions.Add(new Opt("", "(Không gắn hồ sơ)"));
            foreach (var p in s.Profiles) MarketOptions.Add(new Opt(p.Id, string.IsNullOrEmpty(p.Country) ? p.Name : $"{p.Name} ({p.Country})"));
            AccountMarketId = MarketOptions.Any(o => o.Value == current) ? current : "";
        }
        finally { _marketLoading = false; }
        SelectedMarket = Markets.FirstOrDefault(m => m.Id == keep);
    }

    /// <summary>Nạp hồ sơ của tài khoản vào ComboBox (không ghi lại) và tính cảnh báo lệch quốc gia.</summary>
    private async Task LoadAccountMarketAsync(string accountId)
    {
        _marketLoading = true;
        try { AccountMarketId = _settings.Get<MarketSettings>(MarketSettings.Section).Accounts.GetValueOrDefault(accountId) ?? ""; }
        finally { _marketLoading = false; }
        await RefreshMarketWarningAsync(accountId);
    }

    private async Task RefreshMarketWarningAsync(string accountId)
    {
        MarketWarning = null;
        if (_settings.Get<MarketSettings>(MarketSettings.Section).ProfileOf(accountId) is not { Country.Length: 2 } profile) return;
        var conn = await _connections.GetAsync(accountId, CancellationToken.None);
        var policy = AccountIpPolicy.FromJson(conn?.IpPolicyJson);
        var route = policy is null ? null : _broker.ListRoutes().FirstOrDefault(r => r.Id == policy.RouteId);
        // Cùng thứ tự ưu tiên với NetworkRouteBroker khi đăng: quốc gia của policy, rồi của tuyến, cuối cùng của hồ sơ.
        var own = policy?.CountryCode ?? route?.ExpectedCountry;
        MarketWarning = own is { Length: > 0 } && !own.Equals(profile.Country, StringComparison.OrdinalIgnoreCase)
            ? $"Hồ sơ \"{profile.Name}\" cần IP {profile.Country} nhưng chính sách IP/tuyến của tài khoản đặt {own}; khi đăng app kiểm theo {own}."
            : own is null or "" && policy?.IpMode != IpModes.Pinned
                ? $"Khi đăng, app kiểm IP ra ngoài phải thuộc {profile.Country} (theo hồ sơ \"{profile.Name}\"); sai thì bài được dời."
                : null;
    }

    [RelayCommand]
    private void NewMarket()
    {
        SelectedMarket = null;
        MarketName = MarketCountry = MarketLanguage = MarketTimeZone = MarketWindows = MarketHashtags = "";
        foreach (var d in MarketDays) d.IsChecked = true;
        MarketDailyLimit = 20;
        MarketTranslate = false;
        MarketTitleTemplate = "{title}";
        MarketCaptionTemplate = "{title}\n{hashtags}";
        MarketPrivacy = "private";
    }

    [RelayCommand]
    private Task SaveMarketAsync() => Guard(async () =>
    {
        var days = MarketDays.Where(d => d.IsChecked).Select(d => d.Day).ToList();
        if (days.Count == 0) throw new InvalidOperationException("Chọn ít nhất một ngày được đăng.");
        var profile = new MarketProfile
        {
            Id = SelectedMarket?.Id ?? Guid.NewGuid().ToString("N"), Name = MarketName, Country = MarketCountry, Language = MarketLanguage,
            TimeZoneId = MarketTimeZone, Windows = MarketProfile.ParseWindows(MarketWindows), Days = days.Count == 7 ? [] : days,
            DailyLimit = MarketDailyLimit, TranslateText = MarketTranslate, Hashtags = MarketHashtags, TitleTemplate = MarketTitleTemplate,
            CaptionTemplate = MarketCaptionTemplate, Privacy = MarketPrivacy
        };
        try
        {
            profile.Normalize();
            SuperMOOO.Workflows.AutoReup.ReupRuleConfig.CheckTemplates(profile.TitleTemplate, profile.CaptionTemplate);
        }
        catch (ArgumentException e) { throw new InvalidOperationException(e.Message, e); }

        var s = _settings.Get<MarketSettings>(MarketSettings.Section);
        if (s.Profiles.Any(p => p.Id != profile.Id && p.Name.Equals(profile.Name, StringComparison.CurrentCultureIgnoreCase)))
            throw new InvalidOperationException("Đã có hồ sơ cùng tên.");
        var i = s.Profiles.FindIndex(p => p.Id == profile.Id);
        if (i >= 0) s.Profiles[i] = profile; else s.Profiles.Add(profile);
        await _settings.SetAsync(MarketSettings.Section, s);
        ReloadMarkets();
        SelectedMarket = Markets.FirstOrDefault(m => m.Id == profile.Id);
        if (SelectedAccount is { } a) await RefreshMarketWarningAsync(a.Id);
        _dialogs.Info("Hồ sơ thị trường", $"Đã lưu hồ sơ \"{profile.Name}\".");
    });

    [RelayCommand]
    private Task DeleteMarketAsync() => Guard(async () =>
    {
        if (SelectedMarket is not { } m) throw new InvalidOperationException("Chọn một hồ sơ.");
        var s = _settings.Get<MarketSettings>(MarketSettings.Section);
        var used = s.Accounts.Count(x => x.Value == m.Id);
        if (!await _dialogs.ConfirmAsync("Xoá hồ sơ thị trường",
                $"Xoá hồ sơ \"{m.Name}\"?" + (used > 0 ? $" {used} tài khoản đang gắn hồ sơ này sẽ thành không gắn hồ sơ." : "")
                + " Kênh trong luồng Reup chọn riêng hồ sơ này sẽ dùng cài đặt chung của luồng.")) return;
        s.Profiles.RemoveAll(p => p.Id == m.Id);
        foreach (var key in s.Accounts.Where(x => x.Value == m.Id).Select(x => x.Key).ToList()) s.Accounts.Remove(key);
        await _settings.SetAsync(MarketSettings.Section, s);
        SelectedMarket = null;
        ReloadMarkets();
        NewMarket();
        if (SelectedAccount is { } a) await LoadAccountMarketAsync(a.Id);
        _dialogs.Info("Hồ sơ thị trường", $"Đã xoá hồ sơ \"{m.Name}\".");
    });
}

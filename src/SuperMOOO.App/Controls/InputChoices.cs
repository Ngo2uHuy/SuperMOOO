using System.Globalization;
using System.Windows.Media;
using SuperMOOO.AI.Catalog;

namespace SuperMOOO.App.Controls;

public sealed record InputOption(string Value, string Display);

public static class InputChoices
{
    private static readonly Lazy<IReadOnlyList<InputOption>> CountryList = new(BuildCountries);
    private static readonly Lazy<IReadOnlyList<InputOption>> FontList = new(() =>
        Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().Order().Select(f => new InputOption(f, f)).ToList());
    public static IReadOnlyList<InputOption> Countries => CountryList.Value;
    public static IReadOnlyList<InputOption> FontsList => FontList.Value;
    public static IReadOnlyList<InputOption> Languages { get; } =
        [new("", "Không đặt / theo mặc định"), .. LanguageCatalog.All.Select(l => new InputOption(l.Code, $"{l.Name} ({l.Code})")),
            .. LanguageCatalog.All.Select(l => new InputOption(l.Code.Split('-')[0], $"{l.Name.Split('·')[0].Trim()} ({l.Code.Split('-')[0]})"))
                .DistinctBy(l => l.Value).Where(l => !LanguageCatalog.All.Any(known => known.Code == l.Value))];
    public static IReadOnlyList<InputOption> SpeechLanguages { get; } =
        [new("auto", "Tự nhận diện"), .. LanguageCatalog.All.Select(l => new InputOption(LanguageCatalog.SpeechCode(l.Code),
            $"{l.Name.Split('·')[0].Trim()} ({LanguageCatalog.SpeechCode(l.Code)})")).DistinctBy(l => l.Value)];
    public static IReadOnlyList<InputOption> GpuDevices { get; } =
        [.. Enumerable.Range(0, 17).Select(i => new InputOption(i.ToString(CultureInfo.InvariantCulture), $"GPU {i}"))];
    public static IReadOnlyList<InputOption> LoadedModels { get; } = [new("1", "1 model"), new("2", "2 model")];
    public static IReadOnlyList<InputOption> Platforms { get; } =
        [new("", "Tất cả / tự nhận diện"), .. SuperMOOO.Social.Platforms.PlatformRegistry.All.Select(p => new InputOption(p.Id, p.Name))];

    private static IReadOnlyList<InputOption> BuildCountries()
    {
        var names = new Dictionary<string, string>
        {
            ["VN"] = "Việt Nam", ["US"] = "Mỹ", ["GB"] = "Anh", ["CA"] = "Canada", ["AU"] = "Úc",
            ["CN"] = "Trung Quốc", ["TW"] = "Đài Loan", ["HK"] = "Hồng Kông", ["MO"] = "Ma Cao",
            ["JP"] = "Nhật Bản", ["KR"] = "Hàn Quốc", ["KP"] = "Triều Tiên", ["SG"] = "Singapore",
            ["TH"] = "Thái Lan", ["ID"] = "Indonesia", ["MY"] = "Malaysia", ["PH"] = "Philippines",
            ["LA"] = "Lào", ["KH"] = "Campuchia", ["MM"] = "Myanmar", ["IN"] = "Ấn Độ",
            ["FR"] = "Pháp", ["DE"] = "Đức", ["IT"] = "Ý", ["ES"] = "Tây Ban Nha", ["PT"] = "Bồ Đào Nha",
            ["RU"] = "Nga", ["UA"] = "Ukraina", ["NL"] = "Hà Lan", ["BE"] = "Bỉ", ["CH"] = "Thuỵ Sĩ",
            ["AT"] = "Áo", ["PL"] = "Ba Lan", ["CZ"] = "Séc", ["SE"] = "Thuỵ Điển", ["NO"] = "Na Uy",
            ["DK"] = "Đan Mạch", ["FI"] = "Phần Lan", ["IE"] = "Ireland", ["GR"] = "Hy Lạp",
            ["TR"] = "Thổ Nhĩ Kỳ", ["BR"] = "Brazil", ["MX"] = "Mexico", ["AR"] = "Argentina",
            ["NZ"] = "New Zealand", ["ZA"] = "Nam Phi", ["EG"] = "Ai Cập", ["SA"] = "Ả Rập Xê Út",
            ["AE"] = "Các Tiểu vương quốc Ả Rập Thống nhất", ["XK"] = "Kosovo"
        };
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            var region = new RegionInfo(culture.Name);
            if (region.TwoLetterISORegionName is { Length: 2 } code && code.All(char.IsLetter))
                names.TryAdd(code, region.DisplayName);
        }
        return [new("", "Không đặt quốc gia"), .. names.OrderBy(x => x.Value, StringComparer.Create(CultureInfo.GetCultureInfo("vi-VN"), true))
            .Select(x => new InputOption(x.Key, $"{x.Value} ({x.Key})"))];
    }
}

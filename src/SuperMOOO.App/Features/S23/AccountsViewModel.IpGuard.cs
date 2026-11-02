using System.Collections.ObjectModel;
using SuperMOOO.App.Controls;
using SuperMOOO.Core.Publishing;

namespace SuperMOOO.App.Features.S23;

/// <param name="CountryChanged">Quốc gia khác lần thử có quốc gia liền trước (tô màu cảnh báo).</param>
public sealed record IpHistoryEntry(string At, string Route, string Ip, string Country, string Outcome, bool CountryChanged);

/// <summary>Cảnh báo IP của tài khoản: chưa gắn tuyến/quốc gia, nhiều tài khoản chung IP/tuyến, IP đổi quốc gia, và nhật ký IP các lần thử đăng.</summary>
public sealed partial class AccountsViewModel
{
    /// <summary>Quá số tài khoản cùng nền tảng này dùng chung một IP/tuyến thì cảnh báo (nền tảng dễ gom thành cùng chủ).</summary>
    private const int SharedIpWarnThreshold = 3;
    private const int IpHistoryLimit = 100;

    public IReadOnlyList<Opt> CountryChangeOptions { get; } =
        [new(CountryChangeModes.Hold, "Dừng chờ duyệt"), new(CountryChangeModes.Warn, "Chỉ cảnh báo")];

    public ObservableCollection<IpHistoryEntry> IpHistory { get; } = [];

    [ObservableProperty]
    public partial string PolicyCountryChange { get; set; } = CountryChangeModes.Hold;

    /// <summary>Quốc gia mới đang chờ chấp nhận của tài khoản đang xem (null = không có).</summary>
    [ObservableProperty]
    public partial string? PendingCountry { get; set; }

    [RelayCommand]
    private Task AcceptCountryAsync() => Guard(async () =>
    {
        var a = Require();
        var country = await _ipGuard.AcceptAsync(a.Id, CancellationToken.None)
            ?? throw new InvalidOperationException("Tài khoản không có quốc gia mới nào đang chờ chấp nhận.");
        PendingCountry = null;
        Status = $"Đã chấp nhận quốc gia {country} cho \"{a.Name}\"; bài dời sẽ tự thử lại.";
    });

    /// <summary>Cảnh báo theo id tài khoản cho danh sách và chi tiết.</summary>
    private async Task<Dictionary<string, string>> AccountWarningsAsync(IReadOnlyList<SocialConnection> connections, IReadOnlyList<NetworkRoute> routes)
    {
        var markets = _settings.Get<MarketSettings>(MarketSettings.Section);
        var profiles = _settings.Get<NetworkSettings>("network").Profiles;
        var egress = (await _publications.ListLatestEgressAsync(CancellationToken.None)).ToDictionary(e => e.ConnectionId, e => e.EgressIp);
        var lines = connections.ToDictionary(c => c.Id, _ => new List<string>());
        var byRoute = new Dictionary<(SocialPlatform, string), (string Label, List<string> Ids)>();
        foreach (var c in connections)
        {
            // Cùng cách AccountRoute đọc policy: route_profile_id trỏ tới một tuyến mạng cũng là tuyến chính.
            var policy = AccountIpPolicy.FromJson(c.IpPolicyJson)
                ?? (c.RouteProfileId is { } rid && routes.Any(r => r.Id == rid) ? new AccountIpPolicy(rid) : null);
            var route = policy is null ? null : routes.FirstOrDefault(r => r.Id == policy.RouteId);
            string key, label;
            // Cùng tham chiếu (proxy/VPN) thì cùng IP dù khai thành nhiều tuyến; tuyến "trực tiếp" chính là mạng thẳng.
            if (route is not null && route.Kind != NetworkRoute.Direct) (key, label) = ($"{route.Kind}:{route.Ref}", route.Name);
            else if (policy is null && c.RouteProfileId is { } pid)
                (key, label) = ($"{NetworkRoute.Proxy}:{pid}", profiles.FirstOrDefault(p => p.Id == pid)?.Name ?? pid);
            else (key, label) = ("", "mạng thẳng");
            if (!byRoute.TryGetValue((c.Platform, key), out var group)) byRoute[(c.Platform, key)] = group = (label, []);
            group.Ids.Add(c.Id);

            if (policy is null && c.RouteProfileId is null && markets.RequirementFor(c.Id) is null)
                lines[c.Id].Add("Chưa gắn tuyến hay quốc gia: app không chặn được nếu IP bị đổi.");
        }

        var routeWarned = new HashSet<string>();
        foreach (var ((platform, _), (label, ids)) in byRoute.Where(g => g.Value.Ids.Count > SharedIpWarnThreshold))
            foreach (var id in ids)
            {
                lines[id].Add($"{ids.Count} tài khoản {platform} dùng chung IP/tuyến '{label}': dễ bị coi là cùng chủ.");
                routeWarned.Add(id);
            }
        foreach (var group in connections.Where(c => egress.ContainsKey(c.Id)).GroupBy(c => (c.Platform, Ip: egress[c.Id])).Where(g => g.Count() > SharedIpWarnThreshold))
            foreach (var c in group.Where(c => !routeWarned.Contains(c.Id)))
                lines[c.Id].Add($"{group.Count()} tài khoản {group.Key.Platform} dùng chung IP/tuyến '{group.Key.Ip}': dễ bị coi là cùng chủ.");

        return lines.Where(x => x.Value.Count > 0).ToDictionary(x => x.Key, x => string.Join("\n", x.Value));
    }

    private async Task LoadIpHistoryAsync(AccountRow a)
    {
        var attempts = await _publications.ListConnectionAttemptsAsync(a.Id, IpHistoryLimit, CancellationToken.None);
        PendingCountry = await _ipGuard.PendingAsync(a.Id, CancellationToken.None);
        if (SelectedAccount?.Id != a.Id) return;
        var routes = _broker.ListRoutes();
        var profiles = _settings.Get<NetworkSettings>("network").Profiles;
        var rows = new List<IpHistoryEntry>();
        string? previous = null;
        // Danh sách mới nhất trước; so quốc gia theo thứ tự thời gian (dòng cũ hơn liền trước có quốc gia).
        foreach (var x in attempts.Reverse())
        {
            var changed = x.EgressCountry is { Length: > 0 } country && previous is not null && !country.Equals(previous, StringComparison.OrdinalIgnoreCase);
            if (x.EgressCountry is { Length: > 0 }) previous = x.EgressCountry;
            var route = routes.FirstOrDefault(r => r.Id == x.RouteRef)?.Name ?? profiles.FirstOrDefault(p => p.Id == x.RouteRef)?.Name;
            var kind = x.RouteKind switch { "direct" => "Trực tiếp", "proxy" => "Proxy", "vpn" => "VPN", _ => Labels.Term(x.RouteKind ?? "") };
            rows.Add(new IpHistoryEntry(x.StartedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss"), route is null ? kind : $"{kind} · {route}",
                x.EgressIp ?? "chưa kiểm", x.EgressCountry ?? "", AttemptState(x.Outcome), changed));
        }
        rows.Reverse();
        IpHistory.Clear();
        foreach (var r in rows) IpHistory.Add(r);
    }
}

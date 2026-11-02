using Microsoft.Extensions.Logging;
using SuperMOOO.Core.Models;

namespace SuperMOOO.Publishing.Publications;

/// <summary>
/// Đối chiếu IP ra ngoài của lần đăng sắp tới với lần đăng thành công gần nhất của tài khoản (pub_publish_attempts). Khác quốc gia → mặc định
/// dời bài và tạo mục "Cần xử lý" tới khi người dùng chấp nhận quốc gia mới (S23/S14), vì nền tảng coi tài khoản "nhảy quốc gia" là dấu hiệu
/// bị chiếm/chạy nhiều tài khoản; cùng quốc gia khác IP chỉ ghi chú. Lần đầu (chưa có lịch sử) hoặc chưa kiểm IP thì bỏ qua.
/// </summary>
[Service]
public sealed class IpCountryGuard(IPublicationRepository publications, IActionRequiredRepository actions, IPublishSettingsStore settings, IClock clock,
    ILogger<IpCountryGuard> log)
{
    private static readonly TimeSpan HoldRetry = TimeSpan.FromMinutes(15);

    /// <summary>Trả ghi chú cảnh báo (null = không có); quốc gia đổi mà chưa được chấp nhận → <see cref="AccountRouteUnavailableException"/>.</summary>
    public async Task<string?> CheckAsync(SocialConnection connection, string? ip, string? country, CancellationToken ct)
    {
        if (ip is not { Length: > 0 }) return null;
        var last = await publications.LastSucceededEgressAsync(connection.Id, ct);
        if (last?.EgressIp is not { Length: > 0 } lastIp || lastIp == ip) return null;

        var acceptedKey = SettingKeys.IpCountryAcceptedPrefix + connection.Id;
        var accepted = await settings.GetAsync(acceptedKey, ct);
        // Quốc gia không tra được thì không coi là bằng chứng đổi quốc gia: chỉ ghi chú (chính sách IP/hồ sơ đã chặn nếu có yêu cầu quốc gia).
        if (last.EgressCountry is not { Length: > 0 } from || country is not { Length: > 0 } to || from.Equals(to, StringComparison.OrdinalIgnoreCase))
        {
            // Đã về lại quốc gia cũ: bỏ chấp nhận cũ để lần đổi sau phải hỏi lại.
            if (accepted is { Length: > 0 }) await settings.SetAsync(acceptedKey, "", ct);
            log.LogWarning("IP của tài khoản {Id} đổi {Old} → {New} (quốc gia {Country})", connection.Id, lastIp, ip, country ?? "không tra được");
            return $"IP đổi {lastIp} → {ip} so với lần đăng trước (quốc gia {country ?? "không tra được"}).";
        }

        var message = $"IP đổi quốc gia {from} → {to} so với lần đăng trước ({lastIp} → {ip}).";
        var policy = AccountIpPolicy.FromJson(connection.IpPolicyJson);
        if (policy?.CountryChange == CountryChangeModes.Warn || to.Equals(accepted, StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Tài khoản {Id}: {Message} Vẫn đăng (chỉ cảnh báo hoặc đã chấp nhận).", connection.Id, message);
            return message;
        }

        await settings.SetAsync(SettingKeys.IpCountryPendingPrefix + connection.Id, to.ToUpperInvariant(), ct);
        await actions.RaiseAsync(ActionKinds.IpCountryChanged, connection.Id, $"IP của \"{connection.DisplayName}\" đổi quốc gia {from} → {to}",
            $"{message} Bài được dời. Nếu đúng ý bạn, bấm \"Chấp nhận quốc gia mới\" ở Tài khoản & kênh (S23) hoặc Hộp cần xử lý (S14) để đăng tiếp; "
            + "nếu không, sửa tuyến mạng/VPN của tài khoản.", ct);
        throw new AccountRouteUnavailableException($"{message} Chờ bạn chấp nhận quốc gia mới.", clock.UtcNow + HoldRetry);
    }

    /// <summary>Quốc gia mới đang chờ chấp nhận (null = không có).</summary>
    public async Task<string?> PendingAsync(string connectionId, CancellationToken ct) =>
        await settings.GetAsync(SettingKeys.IpCountryPendingPrefix + connectionId, ct) is { Length: > 0 } c ? c : null;

    /// <summary>Chấp nhận quốc gia đang chờ: bài dời tự đăng ở lượt thử kế tiếp. Trả quốc gia đã chấp nhận (null = không có gì chờ).</summary>
    public async Task<string?> AcceptAsync(string connectionId, CancellationToken ct)
    {
        if (await PendingAsync(connectionId, ct) is not { } country) return null;
        await settings.SetAsync(SettingKeys.IpCountryAcceptedPrefix + connectionId, country, ct);
        await settings.SetAsync(SettingKeys.IpCountryPendingPrefix + connectionId, "", ct);
        await actions.ResolveByRefAsync(ActionKinds.IpCountryChanged, connectionId, ct);
        return country;
    }
}

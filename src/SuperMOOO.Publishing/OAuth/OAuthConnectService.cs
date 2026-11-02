using System.Collections.Concurrent;
using System.Text.Json;
using SuperMOOO.Publishing.Connectors;
using SuperMOOO.Publishing.Contracts;
using Microsoft.Extensions.Logging;
using SuperMOOO.Publishing.Publications;
using IDestinationRepository = SuperMOOO.Core.Persistence.IDestinationRepository;
using IQuotaLedger = SuperMOOO.Core.Persistence.IQuotaLedger;
using SuperMOOO.Publishing.Algorithms;
using SuperMOOO.Publishing.Http;

namespace SuperMOOO.Publishing.OAuth;

/// <summary>
/// Kết nối API qua OAuth desktop. Loopback: Worker mở 127.0.0.1/localhost cổng động + PKCE S256 + state; App mở URL bằng trình duyệt hệ thống
/// (Google chặn OAuth trong embedded webview). WebView (chỉ nền tảng có <see cref="IDesktopTokenFlow"/>, vd Meta): App mở cửa sổ WebView2
/// của chính tài khoản, bắt điều hướng tới trang kết quả cố định và chuyển URL cho Worker kiểm tra state.
/// Đổi code/token qua HttpClient của tài khoản (proxy, limiter, breaker). Meta: một lần đăng nhập tách N Page (+ Instagram liên kết)
/// thành nhiều kênh đích, Page token lưu riêng trong vault.
/// </summary>
[Service]
public sealed class OAuthConnectService(
    IConnectionRepository connections,
    IDestinationRepository destinations,
    OAuthClientStore clients,
    OAuthTokenService tokens,
    ConnectorRegistry registry,
    IOAuthCallbackListenerFactory listeners,
    PublishHttpClients http,
    QuotaGuard quota,
    PublicationService publications,
    IActionRequiredRepository actions,
    ICircuitBreaker breaker,
    IPublishSettingsStore settings,
    IAuditLog audit,
    IClock clock,
    ILogger<OAuthConnectService> logger) : IDisposable
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FinishedRetention = TimeSpan.FromMinutes(30);

    private sealed class Session(string id, string connectionId, DateTimeOffset expiresUtc, CancellationTokenSource cts)
    {
        public string Id { get; } = id;
        public string ConnectionId { get; } = connectionId;
        public DateTimeOffset ExpiresUtc { get; } = expiresUtc;
        public CancellationTokenSource Cts { get; } = cts;
        public volatile string State = OAuthSessionStates.Waiting;
        public volatile string? Message = "Hoàn tất đăng nhập trong trình duyệt.";
        public DateTimeOffset? FinishedUtc;
        /// <summary>Chỉ có ở luồng WebView: URL App bắt được.</summary>
        public TaskCompletionSource<Uri>? Redirect;
    }

    private sealed record Linked(DestinationInfo Info, SocialPlatform Platform, string? TokenRef, string? Token, bool CanPublish);

    private readonly ConcurrentDictionary<string, Session> _sessions = new();

    // --- OAuth client ---

    public async Task<OAuthClientStatusDto> GetClientStatusAsync(GetOAuthClientStatusRequest r, CancellationToken ct)
    {
        var platform = ParsePlatform(r.Platform);
        var client = await clients.GetAsync(platform, ct);
        return new OAuthClientStatusDto(platform.ToString(), client is not null, client is null ? null : OAuthClientStore.Hint(client.ClientId));
    }

    public async Task<OAuthClientStatusDto> SetClientAsync(SetOAuthClientRequest r, CancellationToken ct)
    {
        var platform = ParsePlatform(r.Platform);
        var provider = registry.FindOAuth(platform) ?? throw Invalid("Nền tảng chưa hỗ trợ kết nối API.");
        var client = new OAuthClientCredentials((r.ClientId ?? "").Trim(), (r.ClientSecret ?? "").Trim());
        if (provider.ValidateClient(client) is { } error) throw Invalid(error);
        await clients.SaveAsync(platform, client, ct);
        // Không ghi client id/secret vào audit.
        await audit.WriteAsync("ipc", "oauth.client.set", platform.ToString(), null, ct);
        return new OAuthClientStatusDto(platform.ToString(), true, OAuthClientStore.Hint(client.ClientId));
    }

    // --- Phiên đăng nhập ---

    public async Task<OAuthStartDto> StartAsync(StartOAuthConnectRequest r, CancellationToken ct)
    {
        var connection = await connections.GetAsync(r.ConnectionId, ct) ?? throw NotFound("Không tìm thấy tài khoản.");
        var provider = registry.FindOAuth(connection.Platform) ?? throw Invalid("Nền tảng của tài khoản chưa hỗ trợ kết nối API.");
        var client = await clients.GetAsync(connection.Platform, ct)
                     ?? throw Invalid($"Chưa nhập OAuth Client ID/Secret cho {connection.Platform}.");
        await http.EnsureRouteAsync(connection.Id, ct);

        var isMeta = connection.Platform is SocialPlatform.Facebook or SocialPlatform.Instagram;
        var flow = r.Flow ?? (isMeta ? await PlatformFlags.MetaLoginFlowAsync(settings, ct) : OAuthFlows.Loopback);
        if (flow is not (OAuthFlows.Loopback or OAuthFlows.WebView)) throw Invalid("Cách đăng nhập không hợp lệ.");
        var desktop = provider as IDesktopTokenFlow;
        // Google chặn OAuth trong trình duyệt nhúng: chỉ nền tảng có luồng desktop chính thức mới được mở trong WebView.
        if (flow == OAuthFlows.WebView && desktop is null) throw Invalid("Nền tảng này chỉ đăng nhập qua trình duyệt hệ thống.");

        foreach (var old in _sessions.Values.Where(s => s.ConnectionId == connection.Id && !OAuthSessionStates.IsFinal(s.State)))
            Finish(old, OAuthSessionStates.Cancelled, "Đã thay bằng phiên đăng nhập mới.");
        Cleanup();

        var state = OAuthPkce.NewState();
        var cts = new CancellationTokenSource(SessionLifetime);
        var session = new Session(Guid.NewGuid().ToString("N"), connection.Id, clock.UtcNow + SessionLifetime, cts);
        Uri url;
        if (flow == OAuthFlows.WebView)
        {
            session.Redirect = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Message = "Hoàn tất đăng nhập trong cửa sổ trình duyệt của tài khoản.";
            _sessions[session.Id] = session;
            url = desktop!.BuildDesktopTokenUrl(client, state);
            _ = Task.Run(() => RunDesktopSessionAsync(session, desktop, state), CancellationToken.None);
        }
        else
        {
            var callbackProvider = isMeta ? "meta" : connection.Platform == SocialPlatform.TikTok ? "tiktok" : "youtube";
            var listener = isMeta
                ? listeners.Start(callbackProvider, await PlatformFlags.MetaLoopbackHostAsync(settings, ct))
                : listeners.Start(callbackProvider);
            var verifier = OAuthPkce.NewVerifier();
            _sessions[session.Id] = session;
            url = provider.BuildAuthorizeUrl(client, listener.RedirectUri, state, OAuthPkce.ChallengeS256(verifier));
            _ = Task.Run(() => RunSessionAsync(session, listener, state, verifier), CancellationToken.None);
        }
        await audit.WriteAsync("ipc", "oauth.start", connection.Id, flow, ct);
        return new OAuthStartDto(session.Id, url.AbsoluteUri, session.ExpiresUtc, flow,
            flow == OAuthFlows.WebView ? desktop!.DesktopRedirectUri.AbsoluteUri : null);
    }

    public OAuthSessionDto GetSession(OAuthSessionRequest r) =>
        _sessions.TryGetValue(r.SessionId, out var s)
            ? new OAuthSessionDto(s.Id, s.ConnectionId, s.State, s.Message)
            : throw NotFound("Không tìm thấy phiên đăng nhập.");

    public object? Cancel(OAuthSessionRequest r)
    {
        if (!_sessions.TryGetValue(r.SessionId, out var s)) throw NotFound("Không tìm thấy phiên đăng nhập.");
        Finish(s, OAuthSessionStates.Cancelled, "Đã hủy phiên đăng nhập.");
        return null;
    }

    /// <summary>App chuyển URL vừa bắt được trong WebView; state được kiểm tra ở phiên nền, không tin App.</summary>
    public OAuthSessionDto CompleteDesktop(CompleteOAuthDesktopRequest r)
    {
        if (!_sessions.TryGetValue(r.SessionId, out var s) || s.Redirect is null) throw NotFound("Không tìm thấy phiên đăng nhập trong cửa sổ.");
        if (OAuthSessionStates.IsFinal(s.State)) throw Invalid("Phiên đăng nhập đã kết thúc; bắt đầu lại.");
        if (!Uri.TryCreate(r.RedirectUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || r.RedirectUrl.Length > 8192)
            throw Invalid("URL kết quả đăng nhập không hợp lệ.");
        s.Redirect.TrySetResult(uri);
        return new OAuthSessionDto(s.Id, s.ConnectionId, s.State, s.Message);
    }

    private async Task RunSessionAsync(Session session, IOAuthCallbackListener listener, string state, string verifier)
    {
        try
        {
            var callback = await listener.WaitAsync(state, session.Cts.Token);
            if (callback.Error is not null || string.IsNullOrEmpty(callback.Code))
            {
                Finish(session, OAuthSessionStates.Failed, DeniedMessage(callback.Error));
                return;
            }
            session.State = OAuthSessionStates.Exchanging;
            session.Message = "Đang xác nhận với nền tảng...";
            var connection = await CompleteAsync(session.ConnectionId, callback.Code, verifier, listener.RedirectUri, session.Cts.Token);
            Finish(session, OAuthSessionStates.Connected, $"Đã kết nối {connection.DisplayName}.");
        }
        catch (Exception e)
        {
            Fail(session, e);
        }
        finally
        {
            await listener.DisposeAsync();
        }
    }

    private async Task RunDesktopSessionAsync(Session session, IDesktopTokenFlow flow, string state)
    {
        try
        {
            var uri = await session.Redirect!.Task.WaitAsync(session.Cts.Token);
            var result = flow.ParseDesktopRedirect(uri, state);
            if (result is null)
            {
                Finish(session, OAuthSessionStates.Failed, "Cửa sổ chưa tới trang kết quả đăng nhập; bắt đầu lại.");
                return;
            }
            if (result.AccessToken is null)
            {
                Finish(session, OAuthSessionStates.Failed, result.Error == "state_mismatch"
                    ? "Phiên đăng nhập không hợp lệ hoặc đã hết hạn; bắt đầu lại từ CenterPost."
                    : DeniedMessage(result.Error));
                return;
            }
            session.State = OAuthSessionStates.Exchanging;
            session.Message = "Đang xác nhận với nền tảng...";
            var connection = await CompleteDesktopTokenAsync(session.ConnectionId, flow, result.AccessToken, session.Cts.Token);
            Finish(session, OAuthSessionStates.Connected, $"Đã kết nối {connection.DisplayName}.");
        }
        catch (Exception e)
        {
            Fail(session, e);
        }
    }

    private static string DeniedMessage(string? error) => error == "access_denied"
        ? "Bạn đã từ chối cấp quyền trên trang đăng nhập."
        : $"Đăng nhập không thành công ({PublishText.Truncate(error, 100)}).";

    private void Fail(Session session, Exception e)
    {
        switch (e)
        {
            case OperationCanceledException:
                if (!OAuthSessionStates.IsFinal(session.State)) Finish(session, OAuthSessionStates.Expired, "Phiên đăng nhập đã hết hạn.");
                break;
            case ConnectorException or PublishingException or NetworkRequestBlockedException:
                Finish(session, OAuthSessionStates.Failed, e.Message);
                break;
            default:
                logger.LogError(e, "Phiên OAuth {SessionId} lỗi", session.Id);
                Finish(session, OAuthSessionStates.Failed, "Lỗi không xác định khi kết nối; xem log Worker.");
                break;
        }
    }

    private void Finish(Session session, string state, string message)
    {
        session.State = state;
        session.Message = message;
        session.FinishedUtc = clock.UtcNow;
        if (!session.Cts.IsCancellationRequested) session.Cts.Cancel();
    }

    private void Cleanup()
    {
        foreach (var s in _sessions.Values.Where(s => s.FinishedUtc is { } f && clock.UtcNow - f > FinishedRetention))
            if (_sessions.TryRemove(s.Id, out _)) s.Cts.Dispose();
    }

    /// <summary>Đổi code → token, xác minh danh tính, lưu token (giữ refresh token cũ nếu thiếu), đồng bộ kênh đích, xếp lại bài bị chặn.</summary>
    public async Task<SocialConnection> CompleteAsync(string connectionId, string code, string codeVerifier, Uri redirectUri, CancellationToken ct)
    {
        var (connection, provider, client, httpClient) = await PrepareCompleteAsync(connectionId, ct);
        var fresh = await provider.ExchangeCodeAsync(httpClient, client, code, redirectUri, codeVerifier, clock.UtcNow, ct);
        return await LinkAsync(connection, provider, httpClient, fresh, ct);
    }

    /// <summary>Luồng WebView: token ngắn hạn từ trang kết quả → token dài hạn → như <see cref="CompleteAsync"/>.</summary>
    public async Task<SocialConnection> CompleteDesktopTokenAsync(string connectionId, IDesktopTokenFlow flow, string accessToken, CancellationToken ct)
    {
        var (connection, provider, client, httpClient) = await PrepareCompleteAsync(connectionId, ct);
        var fresh = await flow.ExchangeDesktopTokenAsync(httpClient, client, accessToken, clock.UtcNow, ct);
        return await LinkAsync(connection, provider, httpClient, fresh, ct);
    }

    private async Task<(SocialConnection, IOAuthProvider, OAuthClientCredentials, HttpClient)> PrepareCompleteAsync(string connectionId, CancellationToken ct)
    {
        var connection = await connections.GetAsync(connectionId, ct) ?? throw NotFound("Tài khoản đã bị xóa.");
        var provider = registry.FindOAuth(connection.Platform) ?? throw Invalid("Nền tảng chưa hỗ trợ kết nối API.");
        var client = await clients.GetAsync(connection.Platform, ct) ?? throw Invalid("Chưa cấu hình OAuth client.");
        // Người dùng chủ động kết nối lại: không để breaker mở từ lỗi xác thực cũ chặn lần đổi token này.
        breaker.Reset(connection.Id);
        return (connection, provider, client, await http.GetForConnectionAsync(connection.Id, ct));
    }

    private async Task<SocialConnection> LinkAsync(SocialConnection connection, IOAuthProvider provider, HttpClient httpClient, OAuthTokenSet fresh,
        CancellationToken ct)
    {
        // Kiểm tra danh tính bằng token mới trước khi ghi đè bất cứ gì.
        var (external, linked) = await DiscoverAsync(connection, provider, httpClient, fresh.AccessToken, ct);
        if (connection.ExternalAccountId is not null && connection.ExternalAccountId != external)
            throw Invalid("Bạn vừa đăng nhập một tài khoản khác với tài khoản đã gắn ở đây. Tạo tài khoản mới trong CenterPost cho tài khoản đó.");
        if (await connections.FindByExternalAsync(provider.Provider, external, ct) is { } other && other.Id != connection.Id)
            throw Invalid(provider is IMultiDestinationOAuthProvider
                ? $"Tài khoản Meta này đã được kết nối ở \"{other.DisplayName}\" (kết nối Facebook đã gồm cả Instagram liên kết với Page)."
                : $"Kênh này đã được kết nối ở tài khoản \"{other.DisplayName}\".");

        var saved = await tokens.SaveMergedAsync(connection.Id, fresh, ct);
        var detail = saved.RefreshToken is null
            ? provider is IMultiDestinationOAuthProvider
                ? "Token Meta không có refresh token: CenterPost nhắc đăng nhập lại 7 ngày trước khi hết hạn."
                : "Nền tảng không cấp refresh token: sẽ phải kết nối lại khi access token hết hạn."
            : null;
        var scopes = saved.Scope ?? string.Join(' ', provider.Scopes);
        await connections.UpdateOAuthAsync(connection.Id, ApiStatus.Connected, provider.Provider, external, scopes,
            saved.ExpiresUtc, detail, clock.UtcNow, ct);
        await SaveLinkedAsync(connection, linked, scopes, ct);

        breaker.Reset(connection.Id);
        foreach (var kind in new[] { ActionKinds.ConnectionNeedsReauth, ActionKinds.CircuitOpen, ActionKinds.ConnectionTokenExpiring })
            await actions.ResolveByRefAsync(kind, connection.Id, ct);
        var requeued = await publications.RequeueAuthBlockedAsync(connection.Id, ct);
        await audit.WriteAsync("worker", "oauth.connected", connection.Id, $"channels={linked.Count}; requeued={requeued}", ct);
        return (await connections.GetAsync(connection.Id, ct))!;
    }

    /// <summary>Định danh tài khoản + các đích của lần đăng nhập. Meta: Page (+ Instagram liên kết) kèm Page token; nền tảng khác: kênh của token.</summary>
    private async Task<(string External, IReadOnlyList<Linked> Linked)> DiscoverAsync(SocialConnection connection, IOAuthProvider provider,
        HttpClient httpClient, string accessToken, CancellationToken ct)
    {
        if (provider is IMultiDestinationOAuthProvider multi)
        {
            var external = await multi.GetAccountIdAsync(httpClient, accessToken, ct);
            // Kết nối Facebook gồm Page và Instagram liên kết; kết nối Instagram chỉ lấy tài khoản Instagram.
            var granted = (await multi.ListGrantedDestinationsAsync(httpClient, accessToken, ct))
                .Where(g => connection.Platform == SocialPlatform.Facebook || g.Platform == connection.Platform).ToList();
            if (granted.Count == 0)
                throw Invalid(connection.Platform == SocialPlatform.Instagram
                    ? "Không có tài khoản Instagram business/creator nào liên kết với Page bạn đã cấp quyền: liên kết Instagram với Page rồi đăng nhập lại."
                    : "Bạn chưa cấp quyền Page nào cho app: đăng nhập lại và chọn Page ở bước cấp quyền của Meta.");
            return (external, granted.Select(g => new Linked(
                new DestinationInfo(g.RemoteId, g.Title, g.Url, null, g.Attributes), g.Platform, g.TokenRef, g.AccessToken, g.CanPublish)).ToList());
        }

        var connector = registry.Find(connection.Platform) ?? throw Invalid("Nền tảng chưa có connector.");
        var channels = await connector.ListDestinationsAsync(new ConnectorSession(connection.Id, httpClient, new FixedTokenSource(accessToken)), ct);
        await quota.ConsumeAsync(connection, 1, ct);
        if (channels.Count == 0)
            throw Invalid(connection.Platform == SocialPlatform.YouTube
                ? "Tài khoản Google này chưa có kênh YouTube: tạo kênh trên YouTube rồi kết nối lại."
                : "Nền tảng không trả tài khoản/kênh nào cho token này.");
        return (channels[0].RemoteId, channels.Select(c => new Linked(c, connection.Platform, null, null, true)).ToList());
    }

    /// <summary>Lưu token riêng của đích rồi đồng bộ kênh (kênh không còn trong danh sách → removed, không xóa).</summary>
    private async Task SaveLinkedAsync(SocialConnection connection, IReadOnlyList<Linked> linked, string? scopes, CancellationToken ct)
    {
        foreach (var l in linked.Where(l => l.TokenRef is not null && l.Token is not null).DistinctBy(l => l.TokenRef))
            await tokens.SaveDestinationTokenAsync(connection.Id, l.TokenRef!, l.Token!, ct);

        var snapshots = new List<DestinationSnapshot>();
        foreach (var l in linked)
        {
            var attributes = new Dictionary<string, string>(l.Info.Attributes);
            if (l.TokenRef is not null) attributes[DestinationCapabilities.TokenRefAttribute] = l.TokenRef;
            if (!l.CanPublish) attributes[DestinationCapabilities.CanPublishAttribute] = "false";
            var connector = registry.Find(l.Platform);
            var caps = connector is null ? [] : connector.GetCapabilities(scopes);
            snapshots.Add(new DestinationSnapshot(l.Info.RemoteId, l.Info.Title, l.Info.Url, l.Info.UploadsPlaylistId,
                JsonSerializer.Serialize(attributes), SerializeCaps(caps), l.Platform));
        }
        await destinations.SyncAsync(connection.Id, connection.Platform, snapshots, clock.UtcNow, ct);
    }

    private static string SerializeCaps(IEnumerable<CapabilityInfo> caps) =>
        JsonSerializer.Serialize(caps.Select(c => new CapabilityDto(c.Capability.ToString(), c.Status.ToString(), c.Note)).ToList(), PublishingJson.Options);

    public async Task<object?> DisconnectAsync(DisconnectConnectionRequest r, CancellationToken ct)
    {
        var connection = await connections.GetAsync(r.ConnectionId, ct) ?? throw NotFound("Không tìm thấy tài khoản.");
        var token = await tokens.LoadAsync(connection.Id, ct);
        if (r.RevokeAtPlatform && token is not null)
        {
            var provider = registry.FindOAuth(connection.Platform) ?? throw Invalid("Nền tảng chưa hỗ trợ thu hồi quyền.");
            breaker.Reset(connection.Id);
            try
            {
                var httpClient = await http.GetForConnectionAsync(connection.Id, ct);
                await provider.RevokeAsync(httpClient, await RevokeTokenAsync(connection, provider, token, httpClient, ct), ct);
            }
            catch (Exception e) when (e is ConnectorException or NetworkRequestBlockedException)
            {
                throw Invalid($"Không thu hồi được quyền tại nền tảng: {e.Message} Token trên máy chưa bị xóa; chọn \"Chỉ xóa trên máy\" nếu muốn.");
            }
        }

        await tokens.DeleteAsync(connection.Id, ct);
        foreach (var tokenRef in (await destinations.ListAsync(connection.Id, true, ct)).Select(DestinationCapabilities.TokenRef).OfType<string>().Distinct())
            await tokens.DeleteDestinationTokenAsync(connection.Id, tokenRef, ct);
        var status = r.RevokeAtPlatform ? ApiStatus.Revoked : ApiStatus.NotConnected;
        // Giữ external id: kết nối lại phải đúng kênh cũ để không trộn kênh đích/publication của hai kênh.
        await connections.UpdateOAuthAsync(connection.Id, status, connection.Provider, connection.ExternalAccountId, null, null,
            r.RevokeAtPlatform ? "Đã thu hồi quyền tại nền tảng." : "Đã xóa token trên máy.", clock.UtcNow, ct);
        await publications.BlockPendingForConnectionAsync(connection.Id, "Tài khoản đã ngắt kết nối API; kết nối lại để tiếp tục.", ct);
        foreach (var kind in new[] { ActionKinds.ConnectionNeedsReauth, ActionKinds.ConnectionTokenExpiring })
            await actions.ResolveByRefAsync(kind, connection.Id, ct);
        await audit.WriteAsync("ipc", r.RevokeAtPlatform ? "oauth.revoke" : "oauth.disconnect", connection.Id, null, ct);
        return null;
    }

    /// <summary>
    /// Google thu hồi bằng refresh token (gỡ cả grant). TikTok bắt buộc ACCESS token (24h): hết hạn thì làm mới trước.
    /// Meta không có refresh token: dùng user token.
    /// </summary>
    private async Task<string> RevokeTokenAsync(SocialConnection connection, IOAuthProvider provider, StoredOAuthToken token, HttpClient httpClient,
        CancellationToken ct)
    {
        if (connection.Platform != SocialPlatform.TikTok) return token.RefreshToken ?? token.AccessToken;
        if (token.ExpiresUtc is { } exp && exp > clock.UtcNow + OAuthTokenService.RefreshAhead || token.RefreshToken is null) return token.AccessToken;
        if (await clients.GetAsync(connection.Platform, ct) is not { } client) return token.AccessToken;
        return (await provider.RefreshAsync(httpClient, client, token.RefreshToken, clock.UtcNow, ct)).AccessToken;
    }

    // --- Kênh đích ---

    public async Task<List<DestinationDto>> SyncDestinationsAsync(SyncDestinationsRequest r, CancellationToken ct)
    {
        var connection = await connections.GetAsync(r.ConnectionId, ct) ?? throw NotFound("Không tìm thấy tài khoản.");
        if (connection.ApiStatus != ApiStatus.Connected) throw Invalid("Tài khoản chưa kết nối API.");
        var provider = registry.FindOAuth(connection.Platform) ?? throw Invalid("Nền tảng chưa hỗ trợ kết nối API.");
        await http.EnsureRouteAsync(connection.Id, ct);

        try
        {
            var httpClient = await http.GetForConnectionAsync(connection.Id, ct);
            IReadOnlyList<Linked> linked;
            if (provider is IMultiDestinationOAuthProvider)
            {
                // Page token mới được cấp lại theo user token hiện tại (Page mới thêm quyền, Instagram mới liên kết).
                var userToken = await tokens.SourceFor(connection).GetAsync(ct);
                var (external, found) = await DiscoverAsync(connection, provider, httpClient, userToken, ct);
                if (connection.ExternalAccountId is not null && external != connection.ExternalAccountId)
                    throw Invalid("Token không còn thuộc tài khoản đã gắn; kết nối lại.");
                linked = found;
            }
            else
            {
                var connector = registry.Find(connection.Platform) ?? throw Invalid("Nền tảng chưa có connector.");
                await quota.ConsumeAsync(connection, 1, ct);
                var session = new ConnectorSession(connection.Id, httpClient, tokens.SourceFor(connection));
                linked = (await connector.ListDestinationsAsync(session, ct)).Select(c => new Linked(c, connection.Platform, null, null, true)).ToList();
            }
            await SaveLinkedAsync(connection, linked, connection.Scopes, ct);
        }
        catch (ConnectorException e) when (e.Kind == ConnectorErrorKind.Auth)
        {
            await tokens.MarkNeedsReauthAsync(connection, e.Message, ct);
            throw Invalid(e.Message);
        }
        catch (Exception e) when (e is ConnectorException or NetworkRequestBlockedException)
        {
            throw Invalid($"Không đồng bộ được kênh: {e.Message}");
        }
        return await ListDestinationsAsync(new ListDestinationsRequest(connection.Id), ct);
    }

    /// <summary>Capability tính lại lúc đọc (quyền hiện tại + cờ audit + media host), không dùng snapshot cũ.</summary>
    public async Task<List<DestinationDto>> ListDestinationsAsync(ListDestinationsRequest r, CancellationToken ct)
    {
        var all = (await connections.ListAsync(ct)).ToDictionary(c => c.Id);
        var list = new List<DestinationDto>();
        foreach (var d in await destinations.ListAsync(r.ConnectionId, includeRemoved: r.ConnectionId is not null, ct))
        {
            if (!all.TryGetValue(d.ConnectionId, out var c)) continue;
            List<CapabilityDto> caps;
            string? blocked;
            if (registry.Find(d.Platform) is { } connector)
            {
                var live = DestinationCapabilities.For(connector, c, d);
                caps = live.Select(x => new CapabilityDto(x.Capability.ToString(), x.Status.ToString(), x.Note)).ToList();
                blocked = DestinationCapabilities.AutoPublishBlockedReason(d.Platform, live);
            }
            else
            {
                try { caps = JsonSerializer.Deserialize<List<CapabilityDto>>(d.CapabilitiesJson, PublishingJson.Options) ?? []; }
                catch (JsonException) { caps = []; }
                blocked = "Nền tảng chưa có connector.";
            }
            list.Add(new DestinationDto(d.Id, c.Id, c.DisplayName, c.ApiStatus.ToString(), d.Platform.ToString(), d.RemoteId, d.Title, d.Url,
                d.Enabled, d.RemovedUtc is not null, d.SyncedUtc, caps,
                SupportsDirectPost: d.Platform == SocialPlatform.TikTok && DestinationCapabilities.HasScope(c.Scopes, TikTokPostRules.PublishScope),
                AutoPublishBlockedReason: blocked));
        }
        return list;
    }

    public void Dispose()
    {
        foreach (var s in _sessions.Values)
        {
            if (!s.Cts.IsCancellationRequested) s.Cts.Cancel();
        }
    }

    private static SocialPlatform ParsePlatform(string? value) =>
        Enum.TryParse<SocialPlatform>(value, true, out var p) && Enum.IsDefined(p) ? p : throw Invalid("Nền tảng không hợp lệ.");

    private static PublishingException Invalid(string message) => new(PublishingErrors.InvalidRequest, message);
    private static PublishingException NotFound(string message) => new(PublishingErrors.NotFound, message);
}

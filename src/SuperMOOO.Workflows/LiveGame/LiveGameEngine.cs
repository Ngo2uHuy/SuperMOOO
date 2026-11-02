using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace SuperMOOO.Workflows.LiveGame;

/// <summary>Gold: đang mặc áo vàng; Earned: đã theo dõi/chia sẻ/đăng ký trong phiên nên được đổi áo bằng lệnh. Title: danh hiệu theo tổng xu trong phiên.</summary>
public sealed record LiveGameViewer(string Id, string Name, string Avatar, int Seat, bool Gold, DateTimeOffset LastSeen, DateTimeOffset LastSpeech, DateTimeOffset LastAction = default,
    bool Earned = false, string Title = "");
public sealed record LiveGameEffect(long Seq, string UserId, string Action, string Text, int Count, DateTimeOffset At,
    bool IsGift = false, int Coins = 0, string GiftLabel = "", string? Icon = null, string? Sound = null);
/// <summary>Prayer: Text là lời người xem (để chọn chủ đề cầu an), câu đọc = Template thay {prayer}. Ai: được dùng LLM cho câu cầu an.</summary>
public sealed record LiveGameSpeech(string Text, bool Prayer, DateTimeOffset At, string Template = "{prayer}", bool Ai = true);
public sealed record LiveGameAudio(long Seq, string Url, string Text, DateTimeOffset Expires);
public sealed record LiveGameSnapshot(string Session, string Status, int ViewerCount, long Incense, long Prayers, long Likes,
    IReadOnlyList<LiveGameViewer> Viewers, IReadOnlyList<LiveGameDonor> Top, IReadOnlyList<LiveGameEffect> Effects,
    LiveGameAudio? Audio, string Calendar, string Lighting, LiveGameSettings Settings, LiveGameTheme Theme, bool Simulated, long ViewersVersion,
    string Holiday, string Notice);

[Service]
public sealed class LiveGameEngine(ILiveGameStore store, AppPaths paths, LiveGameGiftCatalog catalog, ILogger<LiveGameEngine> log)
{
    // Trần bộ nhớ của sổ tấm lòng trong phiên; người mới sau trần được cộng vào một bản ghi chung để tổng xu vẫn đúng.
    private const int MaxDonors = 10000;
    private const string OthersId = "__others__";
    private const string OthersName = "Người tặng khác";
    private const string TestId = "__test__";
    private const string TestName = "Thử luật";
    // TikFinity đôi khi không gửi bản repeatEnd của combo: không có cập nhật quá 4 giây thì chốt theo số lần cuối.
    private static readonly TimeSpan StreakTimeout = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan StreakMemory = TimeSpan.FromSeconds(60);
    private readonly object _gate = new();
    private readonly Dictionary<string, LiveGameViewer> _viewers = [];
    private readonly LinkedList<string> _recent = new();
    private readonly Dictionary<string, LinkedListNode<string>> _activity = [];
    private readonly PriorityQueue<int, int> _freeSeats = new();
    private readonly List<LiveGameDonor> _leaders = [];
    private LiveGameDonor[] _leaderSnapshot = [];
    private LiveGameViewer[] _viewerSnapshot = [];
    private long _viewersVersion, _snapshotVersion = -1;
    private DateOnly _calendarDate;
    private string _calendar = "", _holiday = "";
    private string _donorNotice = "", _speechNotice = "";
    private readonly Dictionary<string, LiveGameDonor> _donors = [];
    private readonly Dictionary<(string User, DateOnly Day), LiveGameDonor> _dailyDonors = [];
    private readonly HashSet<(string User, DateOnly Day)> _dirtyDonors = [];
    private readonly Queue<LiveGameEffect> _effects = [];
    private readonly Queue<DateTimeOffset> _spoken = [];
    private readonly List<string> _welcomes = [];
    private readonly List<string> _thanks = [];
    private readonly Dictionary<(string User, string Gift), (LiveEvent Last, DateTimeOffset Updated, int Counted)> _streaks = [];
    private readonly Dictionary<(string User, string Gift), (int Count, DateTimeOffset At)> _settled = [];
    private readonly Dictionary<(string User, string Rule), DateTimeOffset> _cooldowns = [];
    private readonly Queue<DateTimeOffset> _commands = [];
    private (LiveGameRule Rule, string[] Keys)[] _rules = [];
    private Channel<LiveEvent> _events = CreateEvents();
    private Channel<LiveGameSpeech> _speech = CreateSpeech();
    private LiveGameTheme _theme = new();
    private LiveGameSettings _settings = new();
    private LiveGameSession _session = new("", DateTimeOffset.UtcNow, null, 0, 0, 0);
    private string _status = "Chưa bắt đầu";
    private int _count;
    private long _seq;
    private DateTimeOffset _lastBatch;
    private LiveGameAudio? _audio;
    public event Action<string>? EventReceived;
    public event Action<string>? StatusChanged;

    private static Channel<LiveEvent> CreateEvents() => Channel.CreateBounded<LiveEvent>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private static Channel<LiveGameSpeech> CreateSpeech() => Channel.CreateBounded<LiveGameSpeech>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    public ChannelReader<LiveGameSpeech> Speech => _speech.Reader;
    public void SetBackgrounds(Dictionary<string, string> backgrounds) { lock (_gate) _theme.Backgrounds = backgrounds; }
    public void SetPlaylist(List<string> playlist) { lock (_gate) _theme.Playlist = playlist; }
    public LiveGameTheme Theme { get { lock (_gate) return _theme; } }
    public string SessionId { get { lock (_gate) return _session.Id; } }
    public void SetSpeechNotice(string text) { lock (_gate) _speechNotice = text; }

    public async Task BeginAsync(LiveGameSettings settings, bool simulated, CancellationToken ct)
    {
        lock (_gate)
        {
            _settings = settings;
            _theme = LiveGameTheme.Load(paths, settings.ThemeId, log);
            _rules = Compile(settings.Rules);
            _events = CreateEvents(); _speech = CreateSpeech();
            _viewers.Clear(); _donors.Clear(); _dailyDonors.Clear(); _dirtyDonors.Clear(); _effects.Clear(); _spoken.Clear(); _welcomes.Clear(); _thanks.Clear();
            _recent.Clear(); _activity.Clear(); _freeSeats.Clear(); _leaders.Clear(); _leaderSnapshot = []; _viewerSnapshot = [];
            _streaks.Clear(); _settled.Clear(); _cooldowns.Clear(); _commands.Clear();
            for (var seat = 0; seat < 1200; seat++) _freeSeats.Enqueue(seat, seat);
            _viewersVersion = 0; _snapshotVersion = -1;
            _audio = null; _seq = 0; _count = 0; _lastBatch = DateTimeOffset.UtcNow; _donorNotice = _speechNotice = "";
            _session = new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, null, 0, 0, 0, simulated);
        }
        await store.SaveSessionAsync(_session, ct);
    }

    /// <summary>Đổi luật quà/bình luận ngay trong phiên (S24 "Lưu &amp; áp dụng luật"); cấu hình khác vẫn chỉ áp dụng khi bắt đầu phiên.</summary>
    public void UpdateRules(List<LiveGameRule> rules, int commandsPerMinute)
    {
        lock (_gate)
        {
            _settings.Rules = rules;
            _settings.CommandsPerMinute = commandsPerMinute;
            _rules = Compile(rules);
        }
    }

    private static (LiveGameRule, string[])[] Compile(List<LiveGameRule> rules) =>
        rules.Where(r => r.Enabled).Select(r => (r, LiveGameText.Triggers(r.Trigger))).ToArray();

    public ValueTask ReceiveAsync(LiveEvent e, CancellationToken ct) => _events.Writer.WriteAsync(e, ct);
    public bool TrySimulate(LiveEvent e)
    {
        lock (_gate) return _session.Simulated && _session.EndedAt is null && _events.Writer.TryWrite(e);
    }

    /// <summary>Phát sự kiện giả khớp luật qua đúng đường xử lý của engine; cờ Simulated giữ nó ngoài sổ công đức, thống kê và danh hiệu.</summary>
    public bool TryTest(LiveGameRule rule, int coins)
    {
        var first = rule.Trigger.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var id = Guid.NewGuid().ToString("N");
        var e = rule.Source == "chat"
            ? new LiveEvent(LiveEventKind.Chat, TestId, TestName, Text: first.Length > 0 ? first : rule.Label, MessageId: id, At: DateTimeOffset.UtcNow, Simulated: true)
            : new LiveEvent(LiveEventKind.Gift, TestId, TestName, GiftId: rule.GiftId, GiftName: first.Length > 0 ? first : rule.Label,
                Coins: Math.Clamp(coins, 1, 1000000), MessageId: id, At: DateTimeOffset.UtcNow, Simulated: true);
        lock (_gate) return _session.Id.Length > 0 && _session.EndedAt is null && _events.Writer.TryWrite(e);
    }

    public void Status(string text)
    {
        lock (_gate) _status = text;
        StatusChanged?.Invoke(text);
    }
    public void SetAudio(string url, string text, TimeSpan duration)
    {
        lock (_gate) _audio = new(++_seq, url, text, DateTimeOffset.UtcNow + duration + TimeSpan.FromSeconds(5));
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        var lastSave = DateTimeOffset.UtcNow;
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                for (var i = 0; i < 150 && _events.Reader.TryRead(out var e); i++)
                {
                    lock (_gate) Apply(e);
                    // Bản cập nhật giữa combo không ghi vào nhật ký; chỉ bản chốt mới là một lần tặng.
                    if (e.Kind == LiveEventKind.Gift && !e.RepeatEnd) continue;
                    var label = e.Kind switch { LiveEventKind.Chat => "Bình luận", LiveEventKind.Gift => "Tặng quà", LiveEventKind.Like => "Thả tim",
                        LiveEventKind.Follow => "Theo dõi", LiveEventKind.Share => "Chia sẻ", LiveEventKind.Subscribe => "Đăng ký", LiveEventKind.Member => "Vào phòng", _ => "Người xem" };
                    EventReceived?.Invoke($"{DateTime.Now:HH:mm:ss} · {(e.Simulated ? "Bắn thử · " : "")}{label} · {LiveGameText.Name(e.Nickname, _settings)}");
                }
                lock (_gate) Tick();
                if (DateTimeOffset.UtcNow - lastSave > TimeSpan.FromSeconds(5))
                {
                    await SaveAsync(false, ct);
                    lastSave = DateTimeOffset.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            _events.Writer.TryComplete();
            lock (_gate)
            {
                while (_events.Reader.TryRead(out var pending)) Apply(pending);
                // Combo đang dở khi dừng phiên vẫn được tính theo số lần cuối nhận được.
                CloseStreaks(DateTimeOffset.MaxValue);
            }
            _speech.Writer.TryComplete();
        }
    }

    public async Task SaveAsync(bool ended, CancellationToken ct)
    {
        LiveGameSession session;
        List<(DateOnly Day, LiveGameDonor Donor)> donors;
        lock (_gate)
        {
            if (ended) _session = _session with { EndedAt = DateTimeOffset.UtcNow };
            session = _session;
            donors = _dirtyDonors.Select(key => (key.Day, _dailyDonors[key])).ToList();
        }
        if (ended) catalog.Flush();
        await store.SaveSessionAsync(session, ct);
        foreach (var (day, donor) in donors)
        {
            await store.SaveDonorAsync(session.Id, day, donor, ct);
            lock (_gate)
            {
                var key = (donor.UserId, day);
                if (_dailyDonors.GetValueOrDefault(key) == donor) _dirtyDonors.Remove(key);
            }
        }
        lock (_gate)
            foreach (var key in _dailyDonors.Keys.Where(k => k.Day < Today() && !_dirtyDonors.Contains(k)).ToArray()) _dailyDonors.Remove(key);
    }

    private void Apply(LiveEvent e)
    {
        var now = DateTimeOffset.UtcNow;
        if (e.Kind == LiveEventKind.ViewerCount) { _count = e.ViewerCount; return; }
        var v = Touch(e, now);
        var throttled = now - v.LastAction < TimeSpan.FromMilliseconds(500);
        switch (e.Kind)
        {
            case LiveEventKind.Member:
                if (_welcomes.Count < 100) _welcomes.Add(v.Name);
                break;
            case LiveEventKind.Chat:
                if (throttled) break;
                _viewers[e.UserId] = v = v with { LastAction = now };
                var text = LiveGameText.Filter(e.Text, _settings);
                if (text.Length == 0) break;
                var folded = LiveGameText.Fold(text);
                var command = _rules.FirstOrDefault(r => r.Rule.Source == "chat" && Matches(folded, r.Keys, r.Rule.Match)).Rule;
                if (command is not null) { Command(e, v, command, now); break; }
                Effect(e.UserId, "chat", text, 1);
                var chant = _theme.ChantKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase));
                if (chant || _theme.PrayerKeywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    if (!e.Simulated) _session = _session with { Prayers = _session.Prayers + 1 };
                    Effect(e.UserId, chant ? "chant" : "bow", "", 1);
                    // Viết tắt được mở rộng chỉ cho phần đưa vào chọn chủ đề/LLM; bong bóng vẫn hiện đúng chữ người xem gõ.
                    if (text.Contains("cầu", StringComparison.OrdinalIgnoreCase)) Say(v, LiveGameText.Expand(text, _theme.Slang), true);
                }
                break;
            case LiveEventKind.Like:
                _session = _session with { Likes = _session.Likes + Math.Max(0, e.LikeCount) };
                if (throttled) break;
                _viewers[e.UserId] = v with { LastAction = now };
                Effect(e.UserId, "heart", "", Math.Clamp(e.LikeCount, 1, 12));
                break;
            case LiveEventKind.Follow:
            case LiveEventKind.Share:
            case LiveEventKind.Subscribe:
                _viewers[e.UserId] = v with { Gold = true, Earned = true };
                Effect(e.UserId, "follow", "", 1);
                if (_thanks.Count < 100) _thanks.Add(v.Name);
                break;
            case LiveEventKind.Gift:
                if (!e.Simulated && !_session.Simulated) catalog.Observe(e);
                if (!e.RepeatEnd) { Streak(e, now); break; }
                var count = Settle(e);
                if (count > 0) Gift(e with { RepeatCount = count }, v);
                break;
        }
    }

    private LiveGameViewer Touch(LiveEvent e, DateTimeOffset now)
    {
        if (!_viewers.TryGetValue(e.UserId, out var v))
        {
            if (_viewers.Count >= 1200) RemoveViewer(_recent.Last!.Value);
            var seat = _freeSeats.Dequeue();
            v = new(e.UserId, LiveGameText.Name(e.Nickname, _settings), e.AvatarUrl, seat, false, now, DateTimeOffset.MinValue,
                Title: e.Simulated ? "" : _theme.Title(_donors.GetValueOrDefault(e.UserId)?.Coins ?? 0));
            _activity[e.UserId] = _recent.AddFirst(e.UserId);
            Effect(e.UserId, "enter", "", 1);
        }
        else
        {
            var node = _activity[e.UserId];
            _recent.Remove(node); _recent.AddFirst(node);
        }
        _viewersVersion++;
        v = v with { LastSeen = now };
        _viewers[e.UserId] = v;
        if (_session.Simulated) _count = _viewers.Count;
        return v;
    }

    private static string GiftKey(LiveEvent e) => e.GiftId.Length > 0 ? e.GiftId : "name:" + e.GiftName;

    private void Streak(LiveEvent e, DateTimeOffset now)
    {
        var key = (e.UserId, GiftKey(e));
        var counted = 0;
        if (_streaks.TryGetValue(key, out var open)) counted = open.Counted;
        else
        {
            // Combo đã chốt vì quá hạn mà vẫn gửi tiếp thì chỉ cộng phần tăng thêm; số lần nhỏ hơn là combo mới.
            if (_settled.Remove(key, out var settled) && e.RepeatCount > settled.Count) counted = settled.Count;
            if (_streaks.Count >= 5000) return;
        }
        _streaks[key] = (e, now, counted);
    }

    /// <summary>Số quà chưa tính của lần tặng vừa kết thúc; 0 nếu combo đã được chốt đủ trước đó (không cộng trùng).</summary>
    private int Settle(LiveEvent e)
    {
        var key = (e.UserId, GiftKey(e));
        if (_streaks.Remove(key, out var open)) return Math.Max(e.RepeatCount, open.Last.RepeatCount) - open.Counted;
        if (_settled.Remove(key, out var settled)) return e.RepeatCount - settled.Count;
        return e.RepeatCount;
    }

    private void CloseStreaks(DateTimeOffset cutoff)
    {
        if (_streaks.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, open) in _streaks.Where(s => cutoff == DateTimeOffset.MaxValue || cutoff - s.Value.Updated > StreakTimeout).ToList())
        {
            _streaks.Remove(key);
            _settled[key] = (open.Last.RepeatCount, now);
            var count = open.Last.RepeatCount - open.Counted;
            if (count > 0) Gift(open.Last with { RepeatCount = count }, Touch(open.Last, now));
        }
    }

    private void Gift(LiveEvent e, LiveGameViewer v)
    {
        var (action, label, matched) = ResolveGift(e);
        if (action is null || e.Coins <= 0 && !matched) return;
        var coins = (long)Math.Max(0, e.Coins) * e.RepeatCount;
        var titleLine = "";
        if (!e.Simulated && coins > 0)
        {
            var (donorId, donorName) = (e.UserId, v.Name);
            if (!_donors.ContainsKey(donorId) && _donors.Count >= MaxDonors)
            {
                (donorId, donorName) = (OthersId, OthersName);
                if (_donorNotice.Length == 0)
                {
                    _donorNotice = $"Phiên đã có {MaxDonors:N0} người tặng; người mới được cộng chung vào \"{OthersName}\"";
                    log.LogWarning("LiveGame đạt {Max} người tặng; gộp người mới vào bản ghi chung", MaxDonors);
                }
            }
            var donor = new LiveGameDonor(donorId, donorName, (_donors.GetValueOrDefault(donorId)?.Coins ?? 0) + coins);
            _donors[donorId] = donor;
            if (donorId != OthersId)
            {
                UpdateLeaders(donor);
                var title = _theme.Title(donor.Coins);
                if (title != v.Title)
                {
                    _viewers[v.Id] = v = v with { Title = title };
                    _viewersVersion++;
                    if (title.Length > 0)
                    {
                        Effect(v.Id, "title", title, 1);
                        titleLine = _theme.Line("title", "{name} nhận danh hiệu {title}.").Replace("{title}", title);
                    }
                }
            }
            var dayKey = (donorId, Today());
            _dailyDonors[dayKey] = new(donorId, donorName, (_dailyDonors.GetValueOrDefault(dayKey)?.Coins ?? 0) + coins);
            _dirtyDonors.Add(dayKey);
            _session = _session with { Incense = _session.Incense + e.RepeatCount };
        }
        var custom = _theme.Action(action);
        var effect = custom?.Effect ?? action;
        Act(e, v, action, label, true, e.RepeatCount);
        var line = custom is { Speech.Length: > 0 } ? custom.Speech : _theme.Line(action, action switch
        {
            "incense" => "{name} tùy hỷ dâng hương.", "bow" => "{name} tùy hỷ lễ Phật.", "lotus" => "{name} tùy hỷ dâng {count} {gift}. {prayer}",
            "festival" => "Tùy hỷ công đức {name}. {prayer}", _ => "{name} tùy hỷ {gift}."
        });
        // Câu lên danh hiệu đi cùng câu cảm ơn để chung một lượt trong giới hạn câu/phút.
        line = (titleLine.Length > 0 ? line + " " + titleLine : line).Replace("{name}", v.Name).Replace("{gift}", LiveGameText.Label(e.GiftName, _settings, label))
            .Replace("{count}", e.RepeatCount.ToString("N0", CultureInfo.GetCultureInfo("vi-VN")));
        var prayer = line.Contains("{prayer}", StringComparison.Ordinal);
        // Đại lễ luôn được cảm ơn dù đã chạm giới hạn câu/phút; chỉ câu cầu an riêng của đại lễ mới dùng LLM.
        Say(v, prayer ? "" : line, prayer, line, effect == "festival", effect == "festival");
    }

    /// <summary>Ưu tiên luật theo mã quà, rồi luật theo tên quà, rồi bảng alias của theme, cuối cùng mức xu. Matched: khớp mã/tên (quà 0 xu vẫn diễn).</summary>
    private (string? Action, string Label, bool Matched) ResolveGift(LiveEvent e)
    {
        if (e.GiftId.Length > 0)
            foreach (var (rule, _) in _rules)
                if (rule.Source == "gift" && rule.GiftId == e.GiftId) return (Known(rule.Action), RuleLabel(rule), true);
        var name = LiveGameText.Fold(e.GiftName);
        if (name.Length > 0)
        {
            foreach (var (rule, keys) in _rules)
                if (rule.Source == "gift" && Matches(name, keys, rule.Match)) return (Known(rule.Action), RuleLabel(rule), true);
            foreach (var alias in _theme.GiftAliases)
                if (alias.Aliases.Contains(name)) return (Known(alias.Action), alias.Label.Length > 0 ? alias.Label : ActionName(alias.Action), true);
        }
        var band = _settings.GiftRules.Where(r => r.MinCoins <= e.Coins).MaxBy(r => r.MinCoins);
        return band is null ? (null, "", false) : (band.Action, band.Label, false);
    }

    private static bool Matches(string folded, string[] keys, string match) =>
        keys.Any(k => match == "contains" ? folded.Contains(k, StringComparison.Ordinal) : folded == k);

    private string Known(string action) => LiveGameActions.IsBuiltIn(action) || _theme.Action(action) is not null ? action : "incense";
    private string ActionName(string action) => _theme.Action(action)?.Label is { Length: > 0 } label ? label
        : LiveGameActions.BuiltIn.FirstOrDefault(a => a.Id == action).Name ?? "quà";
    private string RuleLabel(LiveGameRule rule) => rule.Label.Length > 0 ? rule.Label : ActionName(rule.Action);

    private void Command(LiveEvent e, LiveGameViewer v, LiveGameRule rule, DateTimeOffset now)
    {
        if (!LiveGameActions.IsBuiltIn(rule.Action) && _theme.Action(rule.Action) is null) return;
        // Bắn thử bỏ qua hồi chiêu để bấm thử nhiều lần; người xem thật chịu hồi chiêu riêng và giới hạn chung của phòng.
        if (!e.Simulated)
        {
            var key = (e.UserId, rule.Id);
            if (_cooldowns.TryGetValue(key, out var last) && now - last < TimeSpan.FromSeconds(rule.CooldownSeconds)) return;
            while (_commands.TryPeek(out var old) && now - old >= TimeSpan.FromMinutes(1)) _commands.Dequeue();
            if (_commands.Count >= _settings.CommandsPerMinute) return;
            _commands.Enqueue(now);
            if (_cooldowns.Count >= 20000)
            {
                foreach (var stale in _cooldowns.Where(c => now - c.Value > TimeSpan.FromHours(1)).Select(c => c.Key).ToList()) _cooldowns.Remove(stale);
                if (_cooldowns.Count >= 20000) _cooldowns.Clear();
            }
            _cooldowns[key] = now;
        }
        var label = RuleLabel(rule);
        Act(e, v, rule.Action, label, false, 1);
        if (_theme.Action(rule.Action) is { Speech.Length: > 0 } custom)
        {
            var line = custom.Speech.Replace("{name}", v.Name).Replace("{gift}", label).Replace("{count}", "1");
            var prayer = line.Contains("{prayer}", StringComparison.Ordinal);
            Say(v, prayer ? "" : line, prayer, line, false);
        }
    }

    private void Act(LiveEvent e, LiveGameViewer v, string action, string label, bool isGift, int count)
    {
        if (action == "outfit")
        {
            // Áo vàng là phần thưởng miễn phí của theo dõi/chia sẻ/đăng ký, không đổi được bằng quà; lệnh chỉ đổi qua lại cho người đã có.
            var current = _viewers[v.Id];
            if (!current.Earned && !e.Simulated) return;
            _viewers[v.Id] = current with { Gold = !current.Gold };
            _viewersVersion++;
            Effect(v.Id, "spark", "", 1);
            return;
        }
        var custom = _theme.Action(action);
        Effect(v.Id, custom?.Effect ?? action, isGift ? v.Name : "", Math.Min(12, count), isGift, isGift ? e.Coins : 0, label,
            custom?.Icon is { Length: > 0 } icon ? icon : null, custom?.Sound is { Length: > 0 } sound ? sound : null);
    }

    private void Say(LiveGameViewer? viewer, string text, bool prayer, string template = "{prayer}", bool ai = true, bool priority = false)
    {
        var now = DateTimeOffset.UtcNow;
        while (_spoken.TryPeek(out var old) && now - old >= TimeSpan.FromMinutes(1)) _spoken.Dequeue();
        if (!_settings.EnableSpeech || !priority && _spoken.Count >= _settings.SentencesPerMinute || viewer is not null && now - viewer.LastSpeech < TimeSpan.FromSeconds(30)) return;
        _spoken.Enqueue(now);
        if (viewer is not null) _viewers[viewer.Id] = _viewers[viewer.Id] with { LastSpeech = now };
        _speech.Writer.TryWrite(new(text, prayer, now, template, ai));
    }

    private void Effect(string id, string action, string text, int count, bool isGift = false, int coins = 0, string giftLabel = "", string? icon = null, string? sound = null)
    {
        _effects.Enqueue(new(++_seq, id, action, text, count, DateTimeOffset.UtcNow, isGift, coins, giftLabel, icon, sound));
        while (_effects.Count > 128) _effects.Dequeue();
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        CloseStreaks(now);
        if (_settled.Count > 0)
            foreach (var key in _settled.Where(s => now - s.Value.At > StreakMemory).Select(s => s.Key).ToList()) _settled.Remove(key);
        while (_recent.Last is { } oldest && now - _viewers[oldest.Value].LastSeen > TimeSpan.FromMinutes(10))
        {
            // Người đứng bục vinh danh giữ nhân vật đến hết phiên dù không tương tác.
            if (_leaders.Any(d => d.UserId == oldest.Value))
            {
                _viewers[oldest.Value] = _viewers[oldest.Value] with { LastSeen = now };
                _recent.Remove(oldest); _recent.AddFirst(oldest);
                _viewersVersion++;
                continue;
            }
            RemoveViewer(oldest.Value);
        }
        while (_effects.TryPeek(out var effect) && now - effect.At > TimeSpan.FromSeconds(10)) _effects.Dequeue();
        if (now - _lastBatch < TimeSpan.FromSeconds(20)) return;
        _lastBatch = now;
        if (_welcomes.Count > 0) Say(null, _theme.Line("welcome", "Chào mừng {names} vào chùa.").Replace("{names}", Names(_welcomes)), false);
        if (_thanks.Count > 0) Say(null, _theme.Line("thanks", "Cảm ơn {names} đã theo dõi và chia sẻ.").Replace("{names}", Names(_thanks)), false);
        _welcomes.Clear(); _thanks.Clear();
    }

    private static string Names(List<string> names) => string.Join(", ", names.Take(2)) + (names.Count > 2 ? $" và {names.Count - 2} bạn" : "");
    private static DateOnly Today() => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime);

    private void RemoveViewer(string id)
    {
        var viewer = _viewers[id];
        _viewers.Remove(id);
        _recent.Remove(_activity[id]); _activity.Remove(id);
        _freeSeats.Enqueue(viewer.Seat, viewer.Seat);
        _viewersVersion++;
        if (_session.Simulated) _count = _viewers.Count;
    }

    private void UpdateLeaders(LiveGameDonor donor)
    {
        var old = _leaders.FindIndex(d => d.UserId == donor.UserId);
        if (old >= 0) _leaders.RemoveAt(old);
        var at = _leaders.FindIndex(d => d.Coins < donor.Coins);
        if (at < 0) at = _leaders.Count;
        if (at < 3) _leaders.Insert(at, donor);
        if (_leaders.Count > 3) _leaders.RemoveAt(3);
        _leaderSnapshot = _leaders.ToArray();
    }

    public LiveGameSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(7)).DateTime;
            var date = DateOnly.FromDateTime(now);
            if (_calendarDate != date) { _calendarDate = date; (_calendar, _holiday) = TempleCalendar.Describe(now); }
            if (_snapshotVersion != _viewersVersion)
            {
                _viewerSnapshot = _recent.Select(id => _viewers[id]).ToArray();
                _snapshotVersion = _viewersVersion;
            }
            return new(_session.Id, _status, _count, _session.Incense, _session.Prayers, _session.Likes,
                _viewerSnapshot, _leaderSnapshot,
                _effects.ToArray(), _audio is { } a && a.Expires > DateTimeOffset.UtcNow ? a : null,
                _calendar, now.Hour is >= 6 and < 12 ? "morning" : now.Hour is >= 12 and < 18 ? "afternoon" : "night", _settings, _theme, _session.Simulated, _viewersVersion,
                _holiday, string.Join(" · ", new[] { _donorNotice, _speechNotice }.Where(n => n.Length > 0)));
        }
    }
}

namespace SuperMOOO.Infrastructure.Network;

/// <summary>
/// Khoá đọc–ghi bất đồng bộ giữa tuyến cả máy và traffic mang danh tính tài khoản. VPN Windows/WireGuard đổi IP của cả máy nên là GHI (độc quyền);
/// lease direct/proxy/VPN bật tay, tuyến chung của app và crawler/yt-dlp dùng cookie là ĐỌC (chạy song song). Nhờ vậy tài khoản đi mạng thường
/// không lộ IP quốc gia của VPN đang bật cho tài khoản khác, và kết nối tới proxy không bị đứt vì VPN bật/tắt giữa chừng.
/// Ưu tiên GHI: có VPN đang chờ thì ĐỌC mới phải chờ; VPN của tài khoản chờ quá hạn (lớp trên dời job rồi thử lại) thì vẫn giữ chỗ thêm <see cref="ReserveWindow"/>
/// để các lượt ĐỌC mới không chen mãi. Cùng owner (id tài khoản) được vào lại không chờ: lượt đăng đang giữ khoá xin tuyến lần nữa (làm mới token)
/// sẽ tự khoá chết nếu phải xếp sau VPN đang chờ chính nó nhả.
/// </summary>
[Service]
public sealed class MachineRouteLock
{
    public static readonly TimeSpan ReserveWindow = TimeSpan.FromMinutes(3);
    /// <summary>Thời gian chờ của traffic ngoài luồng đăng mang cookie/token tài khoản (crawler, yt-dlp) khi VPN cả máy đang bật.</summary>
    public static readonly TimeSpan IdentityWait = TimeSpan.FromSeconds(60);

    internal sealed class Waiter(bool write, string? owner, string? routeId, string? name)
    {
        public bool Write { get; } = write;
        public string? Owner { get; } = owner;
        public string? RouteId { get; } = routeId;
        public string? Name { get; } = name;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Quyền đã cấp; Dispose để nhả (một lần).</summary>
    public sealed class Hold : IAsyncDisposable, IDisposable
    {
        private MachineRouteLock? _lock;
        private readonly Waiter _waiter;

        internal Hold(MachineRouteLock owner, Waiter waiter)
        {
            _lock = owner;
            _waiter = waiter;
        }

        /// <summary>Việc chạy khi lượt GHI cuối cùng nhả, trước khi cho ĐỌC vào (tắt VPN do broker bật): ĐỌC không được chạy khi VPN còn bật.</summary>
        public void OnLastRelease(Func<Task> exit)
        {
            if (_lock is { } l && _waiter.Write) l.SetExit(exit);
        }

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _lock, null) is { } l ? new ValueTask(l.ReleaseAsync(_waiter)) : ValueTask.CompletedTask;

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private readonly object _gate = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Dictionary<string, int> _readOwners = new(StringComparer.Ordinal);
    private int _readers;
    private int _writers;
    private string? _writerOwner, _writerRouteId, _writerName;
    private Func<Task>? _writerExit;
    private bool _exiting;
    private DateTimeOffset _reservedUntil;
    private string? _reservedName;

    /// <summary>VPN cả máy đang giữ khoá (null = không có).</summary>
    public (string? RouteId, string? Name, string? Owner)? ActiveWriter
    {
        get
        {
            lock (_gate) return _writers > 0 || _exiting ? (_writerRouteId, _writerName, _writerOwner) : null;
        }
    }

    public Task<Hold> EnterReadAsync(string? owner, TimeSpan wait, CancellationToken ct) => EnterAsync(new Waiter(false, owner, null, null), wait, ct);

    public Task<Hold> EnterWriteAsync(string? owner, NetworkRoute route, TimeSpan wait, CancellationToken ct) =>
        EnterAsync(new Waiter(true, owner, route.Id, route.Name), wait, ct);

    private async Task<Hold> EnterAsync(Waiter w, TimeSpan wait, CancellationToken ct)
    {
        lock (_gate)
        {
            if (CanGrant(w))
            {
                Grant(w);
                return new Hold(this, w);
            }
            _waiters.Add(w);
        }

        try
        {
            await w.Done.Task.WaitAsync(wait, ct);
            return new Hold(this, w);
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
            string message;
            lock (_gate)
            {
                if (_waiters.Remove(w))
                {
                    message = BusyMessage(w);
                    // Chỉ lượt đăng của tài khoản (có owner) được dời rồi thử lại; nút kiểm tra/quét nguồn không quay lại nên không giữ chỗ.
                    if (w.Write && w.Owner is not null && e is TimeoutException)
                    {
                        _reservedUntil = DateTimeOffset.UtcNow + ReserveWindow;
                        _reservedName = w.Name;
                        _ = Task.Delay(ReserveWindow + TimeSpan.FromSeconds(1)).ContinueWith(_ => { lock (_gate) Pump(); }, TaskScheduler.Default);
                    }
                    // Người chờ GHI rút ra có thể mở đường cho ĐỌC đang xếp sau nó.
                    Pump();
                }
                else message = "";
            }
            if (message.Length == 0)
            {
                // Được cấp đúng lúc hết hạn: dùng luôn; bị huỷ thì trả lại.
                if (e is TimeoutException) return new Hold(this, w);
                await ReleaseAsync(w);
            }
            if (e is OperationCanceledException) throw;
            throw new RouteBusyException(message, w.RouteId);
        }
    }

    private bool CanGrant(Waiter w)
    {
        if (_exiting) return false;
        if (w.Write)
        {
            if (_writers > 0) return w.Owner is not null && w.Owner == _writerOwner && w.RouteId == _writerRouteId;
            if (_readers > 0) return false;
            // Các VPN chờ nhau theo thứ tự đến.
            var first = _waiters.FirstOrDefault(x => x.Write);
            return first is null || first == w;
        }
        if (_writers > 0) return w.Owner is not null && w.Owner == _writerOwner;
        if (w.Owner is not null && _readOwners.ContainsKey(w.Owner)) return true;
        if (_waiters.Any(x => x.Write)) return false;
        return _reservedUntil <= DateTimeOffset.UtcNow;
    }

    private void Grant(Waiter w)
    {
        if (w.Write)
        {
            if (_writers++ == 0)
            {
                _writerOwner = w.Owner;
                _writerRouteId = w.RouteId;
                _writerName = w.Name;
            }
            _reservedUntil = default;
            return;
        }
        _readers++;
        if (w.Owner is not null) _readOwners[w.Owner] = _readOwners.GetValueOrDefault(w.Owner) + 1;
    }

    private void Pump()
    {
        for (var i = 0; i < _waiters.Count; i++)
        {
            var w = _waiters[i];
            if (!CanGrant(w)) continue;
            _waiters.RemoveAt(i--);
            Grant(w);
            w.Done.TrySetResult();
        }
    }

    private void SetExit(Func<Task> exit)
    {
        lock (_gate) _writerExit ??= exit;
    }

    private async Task ReleaseAsync(Waiter w)
    {
        Func<Task>? exit;
        lock (_gate)
        {
            if (!w.Write)
            {
                _readers--;
                if (w.Owner is not null && --_readOwners[w.Owner] == 0) _readOwners.Remove(w.Owner);
                Pump();
                return;
            }
            if (--_writers > 0) return;
            exit = _writerExit;
            _writerExit = null;
            if (exit is null)
            {
                ClearWriter();
                Pump();
                return;
            }
            _exiting = true;
        }

        try { await exit(); }
        finally
        {
            lock (_gate)
            {
                _exiting = false;
                ClearWriter();
                Pump();
            }
        }
    }

    private void ClearWriter() => _writerOwner = _writerRouteId = _writerName = null;

    private string BusyMessage(Waiter w)
    {
        if (w.Write)
        {
            if (_writers > 0 || _exiting)
                return _writerRouteId == w.RouteId ? $"Tuyến '{w.Name}' đang được tài khoản khác dùng."
                    : $"Đang có VPN cả máy '{_writerName}' bật cho tài khoản khác; chờ VPN đó tắt rồi mới bật '{w.Name}'.";
            if (_readers > 0)
                return $"Còn {_readers} lượt đăng/đồng bộ của tài khoản khác đang đi mạng thường hoặc proxy; chờ xong mới bật VPN cả máy '{w.Name}' để không đổi IP của chúng.";
            return $"Tuyến '{w.Name}' đang chờ lượt sau VPN cả máy khác.";
        }
        if (_writers > 0 || _exiting) return $"Đang có VPN cả máy '{_writerName}' bật cho tài khoản khác; chờ VPN tắt để không lộ IP.";
        var pending = _waiters.FirstOrDefault(x => x.Write)?.Name ?? _reservedName;
        return $"VPN cả máy '{pending}' đang chờ bật cho tài khoản khác; chờ VPN đó xong để không lộ IP.";
    }
}

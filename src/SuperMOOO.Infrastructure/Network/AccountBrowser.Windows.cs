using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SuperMOOO.Infrastructure.Network;

public sealed record BrowserWindowState(string Status, int WindowCount);

public sealed partial class AccountBrowser : IDisposable
{
    private sealed class Session(Process process, string routeKey)
    {
        public Process? Process { get; set; } = process;
        public string RouteKey { get; } = routeKey;
        public bool SeenWindow { get; set; }
    }

    private readonly object _windowLock = new();
    private readonly Dictionary<string, Session> _sessions = [];

    private void TrackBrowser(string id, Process process, string routeKey)
    {
        lock (_windowLock)
        {
            // Edge chuyển lệnh sang tiến trình đang giữ profile; giữ handle cũ để tránh nhầm PID tái sử dụng.
            if (_sessions.TryGetValue(id, out var current) && current.Process is { HasExited: false })
            {
                process.Dispose();
                return;
            }
            current?.Process?.Dispose();
            _sessions[id] = new Session(process, routeKey);
        }
    }

    private Dictionary<string, List<nint>> FindWindows()
    {
        var result = _sessions.Keys.ToDictionary(id => id, _ => new List<nint>());
        var owners = new Dictionary<int, string>();
        foreach (var (id, session) in _sessions)
        {
            if (session.Process is not { } process) continue;
            if (process.HasExited)
            {
                process.Dispose();
                session.Process = null;
            }
            else owners[process.Id] = id;
        }
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out var pid);
            if (!owners.TryGetValue((int)pid, out var id)) return true;
            var className = new StringBuilder(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() == "Chrome_WidgetWin_1")
            {
                result[id].Add(window);
                _sessions[id].SeenWindow = true;
            }
            return true;
        }, 0);
        return result;
    }

    public IReadOnlyDictionary<string, BrowserWindowState> GetWindowStates()
    {
        lock (_windowLock)
        {
            return FindWindows().ToDictionary(pair => pair.Key, pair => new BrowserWindowState(
                pair.Value.Count > 0 ? "Đang mở" : _sessions[pair.Key].SeenWindow ? "Đã đóng"
                : _sessions[pair.Key].Process is null ? "Không theo dõi được" : "Đang khởi động", pair.Value.Count));
        }
    }

    public BrowserLaunch BringToFront(string id)
    {
        lock (_windowLock)
        {
            var window = FindWindows().GetValueOrDefault(id)?.FirstOrDefault() ?? 0;
            if (window == 0) return new(false, "Không tìm thấy cửa sổ của tài khoản trong phiên app này.");
            ShowWindowAsync(window, 9);
            return SetForegroundWindow(window) ? new(true, "Đã đưa cửa sổ lên trước.")
                : new(false, "Windows chưa cho phép chuyển cửa sổ. Hãy chọn cửa sổ Edge trên thanh tác vụ.");
        }
    }

    public BrowserLaunch ArrangeWindows(IReadOnlyCollection<string> ids, int columns)
    {
        lock (_windowLock)
        {
            var windows = FindWindows().Where(p => ids.Contains(p.Key)).SelectMany(p => p.Value).ToList();
            if (windows.Count == 0) return new(false, "Chưa có cửa sổ đang mở trong các tài khoản đã chọn.");
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(GetForegroundWindow(), 2), ref info))
                return new(false, "Không lấy được vùng làm việc của màn hình.");
            var width = info.Work.Right - info.Work.Left;
            var height = info.Work.Bottom - info.Work.Top;
            var maxColumns = Math.Max(1, width / 320);
            var maxRows = Math.Max(1, height / 240);
            if (columns == 0)
                columns = Math.Clamp((int)Math.Ceiling(Math.Sqrt(windows.Count * (double)width / height)), 1, maxColumns);
            var rows = (int)Math.Ceiling(windows.Count / (double)columns);
            if (columns < 1 || columns > maxColumns || rows > maxRows)
                return new(false, "Không đủ chỗ trên màn hình cho lưới này. Chọn ít tài khoản hơn hoặc đổi số cột.");
            var moved = 0;
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                var x = i % columns;
                var y = i / columns;
                ShowWindow(window, 9);
                if (SetWindowPos(window, 0, info.Work.Left + x * width / columns, info.Work.Top + y * height / rows,
                    (x + 1) * width / columns - x * width / columns, (y + 1) * height / rows - y * height / rows, 0x0014)) moved++;
            }
            return new(moved == windows.Count, $"Đã xếp {moved}/{windows.Count} cửa sổ theo lưới {columns} cột.");
        }
    }

    public void Dispose()
    {
        lock (_windowLock)
        {
            foreach (var session in _sessions.Values) session.Process?.Dispose();
            _sessions.Clear();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder name, int maxCount);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}

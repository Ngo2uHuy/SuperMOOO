using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace SuperMOOO.Infrastructure.Health;

/// <summary>
/// Giới hạn tiến trình media dùng chung giữa pipeline và preview trực tiếp. RAM là ngưỡng giảm song song, không phải hard cap.
/// Đo CPU/RAM mỗi 2 giây (làm mượt) để ở chế độ tự động: hoãn job media bổ sung, giảm luồng FFmpeg mới,
/// hạ ưu tiên tiến trình con khi app khác chiếm CPU và báo RAM cạn để tắt server AI đang rảnh.
/// </summary>
[Service(As = [typeof(IRuntimeResources)])]
public sealed class RuntimeResources : IRuntimeResources, IDisposable
{
    private const long Gb = 1073741824;
    private const double CpuHigh = 85, CpuCalm = 75, OtherBusy = 30, OtherCalm = 15, PriorityRestoreCpu = 70;
    // EMA ~3 mẫu (~6 giây): một đợt tăng vọt ngắn không làm đổi trạng thái qua lại.
    private const double Smoothing = 0.35;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private static readonly int PhysicalCores = ReadPhysicalCores();

    private readonly ISettingsStore _settings;
    private readonly ILogger? _log;
    private readonly object _sync = new();
    private readonly object _sampleSync = new();
    private readonly List<Tracked> _tracked = [];
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Timer _timer;
    private readonly string _cpuName = ReadCpuName();
    private int _active;
    private TaskCompletionSource _mediaChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;
    private ResourceLoad _load;
    private TimeSpan _childrenCpu;
    private bool _primed, _cpuHigh, _ramLow, _ramCritical;
    private long _lastIdle, _lastTotal;
    private TimeSpan _lastOwn;
    private double _cpu, _other, _ram;

    public RuntimeResources(ISettingsStore settings, ILogger<RuntimeResources>? log = null)
    {
        _settings = settings;
        _log = log;
        _load = new ResourceLoad(0, 0, Machine.AvailableMemoryBytes, ResourcePressure.Normal, false);
        _settings.Changed += OnSettingsChanged;
        _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, SampleInterval);
    }

    public event EventHandler<ResourceLoad>? LoadChanged;
    public event EventHandler? MemoryCritical;

    public MachineResources Machine
    {
        get
        {
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (!GlobalMemoryStatusEx(ref memory))
                return new MachineResources(_cpuName, Environment.ProcessorCount, 4L << 30, 0, PhysicalCores);
            return new MachineResources(_cpuName, Environment.ProcessorCount, (long)memory.TotalPhysical, (long)memory.AvailablePhysical, PhysicalCores);
        }
    }
    public PerformancePlan Plan => PerformancePlanner.Calculate(Machine, _settings.Get<PerformanceSettings>("performance"));
    public ResourceLoad Load => Volatile.Read(ref _load);

    public async Task<IDisposable> AcquireMediaAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                changed = _mediaChanged.Task;
            }
            var machine = Machine;
            var plan = PerformancePlanner.Calculate(machine, _settings.Get<PerformanceSettings>("performance"));
            var calm = !plan.Automatic || Load.Pressure == ResourcePressure.Normal;
            lock (_sync)
            {
                // Ít RAM hoặc CPU cao kéo dài: luôn cho một job tiến triển; chỉ tạm hoãn các job bổ sung, tránh chờ vô hạn.
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_active < plan.MediaParallel && (_active == 0 || (calm && machine.AvailableMemoryBytes >= (plan.ReservedMemoryGb + 2L) * Gb)))
                {
                    _active++;
                    return new Lease(this);
                }
            }
            await changed.WaitAsync(ct);
        }
    }

    private void OnSettingsChanged(object? sender, string section)
    {
        if (section == "performance") lock (_sync) WakeMediaWaiters();
    }

    private void WakeMediaWaiters()
    {
        if (_disposed) return;
        var previous = _mediaChanged;
        _mediaChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }

    public int CurrentMediaThreads()
    {
        var plan = Plan;
        var load = Load;
        if (!plan.Automatic || load.Pressure == ResourcePressure.Normal) return plan.MediaThreads;
        // Chỉ trừ phần CPU app khác đang dùng: tải của chính app (bước trước vừa xong) không làm bước sau chậm đi.
        return Math.Clamp((int)Math.Round(plan.MediaThreads * (100 - load.OtherCpuPercent) / 100), 1, plan.MediaThreads);
    }

    public IDisposable TrackProcess(Process process)
    {
        var tracked = new Tracked(this, process) { Cpu = CpuTime(process) ?? TimeSpan.Zero };
        lock (_sync)
        {
            _tracked.Add(tracked);
            if (_load.ChildrenBelowNormal) SetPriority(process, true);
        }
        return tracked;
    }

    private void Untrack(Tracked tracked)
    {
        lock (_sync)
        {
            if (!_tracked.Remove(tracked)) return;
            // Phần CPU từ mẫu trước tới lúc thoát vẫn là của app, không tính sang app khác.
            if (CpuTime(tracked.Process) is { } cpu && cpu > tracked.Cpu) _childrenCpu += cpu - tracked.Cpu;
        }
    }

    private void Sample()
    {
        if (!Monitor.TryEnter(_sampleSync)) return;
        try
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;
            var total = kernel + user;  // thời gian kernel đã gồm idle, cộng mọi lõi
            var own = _self.TotalProcessorTime + ChildrenCpu();
            var machine = Machine;
            if (_primed && total > _lastTotal)
            {
                var span = (double)(total - _lastTotal);
                var cpu = Math.Clamp(100 * (span - (idle - _lastIdle)) / span, 0, 100);
                var other = Math.Clamp(cpu - 100 * (own - _lastOwn).Ticks / span, 0, 100);
                _cpu += Smoothing * (cpu - _cpu);
                _other += Smoothing * (other - _other);
                _ram += Smoothing * (machine.AvailableMemoryBytes - _ram);
            }
            else _ram = machine.AvailableMemoryBytes;
            _primed = true;
            _lastIdle = idle;
            _lastTotal = total;
            _lastOwn = own;
            Evaluate(PerformancePlanner.Calculate(machine, _settings.Get<PerformanceSettings>("performance")));
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { _log?.LogDebug(e, "Đo tải máy lỗi"); }
        finally { Monitor.Exit(_sampleSync); }
    }

    private TimeSpan ChildrenCpu()
    {
        lock (_sync)
        {
            foreach (var child in _tracked)
            {
                if (CpuTime(child.Process) is not { } cpu || cpu <= child.Cpu) continue;
                _childrenCpu += cpu - child.Cpu;
                child.Cpu = cpu;
            }
            return _childrenCpu;
        }
    }

    private void Evaluate(PerformancePlan plan)
    {
        var reserve = plan.ReservedMemoryGb * (double)Gb;
        _cpuHigh = _cpu >= (_cpuHigh ? CpuCalm : CpuHigh);
        _ramCritical = _ram < reserve + (_ramCritical ? Gb / 2 : 0);
        _ramLow = _ram < reserve + (_ramLow ? 2.5 * Gb : 2d * Gb);
        var pressure = _ramCritical ? ResourcePressure.Critical : _cpuHigh || _ramLow ? ResourcePressure.High : ResourcePressure.Normal;

        var previous = Load;
        var lowered = plan.Automatic && (previous.ChildrenBelowNormal
            ? _cpu >= PriorityRestoreCpu && _other >= OtherCalm
            : _cpu >= CpuHigh && _other >= OtherBusy);
        var load = new ResourceLoad(_cpu, _other, (long)_ram, pressure, lowered);
        lock (_sync)
        {
            Volatile.Write(ref _load, load);
            WakeMediaWaiters();
            if (lowered != previous.ChildrenBelowNormal)
                foreach (var child in _tracked) SetPriority(child.Process, lowered);
        }

        if (pressure != previous.Pressure || lowered != previous.ChildrenBelowNormal)
        {
            _log?.LogInformation("Tải máy {Pressure}: CPU {Cpu:0}% (app khác {Other:0}%), RAM trống {Ram:0.0} GB, ưu tiên tiến trình con {Priority}",
                pressure, _cpu, _other, _ram / Gb, lowered ? "BelowNormal" : "Normal");
            LoadChanged?.Invoke(this, load);
        }
        if (pressure == ResourcePressure.Critical && plan.Automatic) MemoryCritical?.Invoke(this, EventArgs.Empty);
    }

    private static void SetPriority(Process process, bool belowNormal)
    {
        try { process.PriorityClass = belowNormal ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { /* đã thoát hoặc không đủ quyền */ }
    }

    private static TimeSpan? CpuTime(Process process)
    {
        try { return process.TotalProcessorTime; }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        lock (_sync)
        {
            _disposed = true;
            _mediaChanged.TrySetResult();
        }
        _timer.Dispose();
        _self.Dispose();
    }

    private sealed class Lease(RuntimeResources owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            lock (owner._sync) { owner._active--; owner.WakeMediaWaiters(); }
        }
    }
    private sealed class Tracked(RuntimeResources owner, Process process) : IDisposable
    {
        private int _disposed;
        public Process Process => process;
        public TimeSpan Cpu { get; set; }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Untrack(this); }
    }
    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? $"CPU {Environment.ProcessorCount} luồng";
    }
    private static int ReadPhysicalCores()
    {
        const int relationProcessorCore = 0;
        uint length = 0;
        GetLogicalProcessorInformationEx(relationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(relationProcessorCore, buffer, ref length)) return 0;
            var cores = 0;
            // Mỗi bản ghi SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX có độ dài thay đổi: Relationship (int) rồi Size (int).
            for (var offset = 0; offset + 8 <= length; cores++)
            {
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0) break;
                offset += size;
            }
            return cores;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}

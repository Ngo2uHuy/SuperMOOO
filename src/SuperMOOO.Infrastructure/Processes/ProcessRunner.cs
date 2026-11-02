using System.Diagnostics;
using System.Text;

namespace SuperMOOO.Infrastructure.Processes;

[Service(As = [typeof(IProcessRunner)])]
public sealed class ProcessRunner(IRuntimeResources? resources = null) : IProcessRunner
{
    private const int TailLines = 100;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, IProgress<string>? lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = spec.WorkingDirectory ?? ""
        };
        foreach (var arg in spec.Arguments) psi.ArgumentList.Add(arg);
        if (spec.Environment is not null)
            foreach (var (key, value) in spec.Environment) psi.Environment[key] = value;

        var tail = new Queue<string>();
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (tail)
            {
                if (tail.Count == TailLines) tail.Dequeue();
                tail.Enqueue(e.Data);
            }

            lines?.Report(e.Data);
        }

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;

        using var job = new WindowsJobObject();
        process.Start();
        var assigned = job.TryAssign(process);
        using var tracked = spec.Heavy ? resources?.TrackProcess(process) : null;
        process.StandardInput.Close();      // tránh tiến trình con chờ stdin vô hạn
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cancel = ct.Register(() =>
        {
            if (assigned) job.Terminate();
            else TryKillTree(process);
        });

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        lock (tail) return new ProcessResult(process.ExitCode, tail.ToList());
    }

    private static void TryKillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* đã thoát */ }
    }
}

using System.Diagnostics;
using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>All executable launches are shell-free and preserve argument boundaries.</summary>
public sealed class HashcatRunner
{
    public static ProcessStartInfo CreateStartInfo(HashcatCommand command, bool interactive = false)
    {
        var info = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = interactive
        };
        foreach (var argument in command.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public async Task<ProcessCapture> CaptureAsync(HashcatCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(command) };
        try
        {
            if (!process.Start()) throw new HashcatException("Hashcat could not be started.");
        }
        catch (System.ComponentModel.Win32Exception) { throw new HashcatException("Hashcat could not be started. Check the executable path and permissions."); }
        using var registration = cancellationToken.Register(() => TryKill(process));
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var errors = await stderr.ConfigureAwait(false);
            if (output.Truncated || errors.Truncated) throw new HashcatException("Hashcat output exceeded the in-memory limit. Narrow the target or inspect the output file locally.");
            return new(process.ExitCode, output.Text, errors.Text);
        }
        finally
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader)
    {
        const int limit = 16 * 1024 * 1024;
        var result = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var take = Math.Min(count, limit - result.Length);
            result.Append(buffer, 0, take);
            truncated |= take != count;
        }
        return (result.ToString(), truncated);
    }

    public HashcatRunningJob Start(HashcatCommand command, IProgress<HashcatEvent>? progress = null, CancellationToken cancellationToken = default, bool interactiveControls = false)
        => new(command, progress, cancellationToken, interactiveControls);

    internal static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* The process may exit between inspection and cancellation. */ }
        catch (System.ComponentModel.Win32Exception) { /* Completion observes the process state if the OS has already reaped it. */ }
    }
}

public sealed class HashcatRunningJob
{
    private readonly Process _process;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _linked;
    private readonly bool _interactiveControls;
    private readonly IProgress<HashcatEvent>? _progress;
    private readonly object _lifecycle = new();
    private bool _disposed;
    public Task<HashcatRunResult> Completion { get; }
    public bool CanSendInteractiveControls => _interactiveControls;

    internal HashcatRunningJob(HashcatCommand command, IProgress<HashcatEvent>? progress, CancellationToken cancellationToken, bool interactiveControls)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _progress = progress;
        _interactiveControls = interactiveControls;
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        _process = new Process { StartInfo = HashcatRunner.CreateStartInfo(command, true) };
        try
        {
            if (!_process.Start()) throw new HashcatException("Hashcat could not be started.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or HashcatException)
        {
            _process.Dispose(); _linked.Dispose(); _stop.Dispose();
            throw new HashcatException("Hashcat could not be started. Check the installation and access permissions.");
        }
        Completion = CompleteAsync();
    }

    public async Task SendControlAsync(HashcatControl control)
    {
        if (!_interactiveControls) throw new HashcatException("Interactive controls are unavailable with this backend's redirected input. Stop is available; a saved restore checkpoint can be resumed after exit.");
        if (Completion.IsCompleted) return;
        var key = control switch { HashcatControl.Pause => 'p', HashcatControl.Resume => 'r', HashcatControl.Checkpoint => 'c', HashcatControl.Status => 's', HashcatControl.Quit => 'q', _ => throw new ArgumentOutOfRangeException(nameof(control)) };
        try { await _process.StandardInput.WriteAsync(key).ConfigureAwait(false); await _process.StandardInput.FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { throw new HashcatException("Hashcat is no longer accepting control input."); }
    }

    public async Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (!_disposed && !Completion.IsCompleted) _stop.Cancel();
        }
        await Completion.ConfigureAwait(false);
    }

    private async Task<HashcatRunResult> CompleteAsync()
    {
        using var registration = _linked.Token.Register(() => HashcatRunner.TryKill(_process));
        try
        {
            var output = ReadLinesAsync(_process.StandardOutput, false);
            var errors = ReadLinesAsync(_process.StandardError, true);
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            var cancelled = _linked.IsCancellationRequested;
            var exit = _process.ExitCode;
            var state = cancelled ? JobState.Cancelled : exit switch { 0 => JobState.Cracked, 1 => JobState.Exhausted, 2 => JobState.Cancelled, 3 => JobState.Checkpointed, 4 => JobState.Interrupted, _ => JobState.Failed };
            var diagnostic = cancelled ? "Stopped by request. Resume is available if Hashcat saved a restore file." : exit switch
            {
                0 => "Hashcat completed successfully; recovered results can be refreshed.",
                1 => "Candidate space exhausted.", 2 => "Hashcat aborted the job.", 3 => "Hashcat stopped at a restore checkpoint.",
                4 => "Hashcat reached its configured runtime limit.",
                _ => $"Hashcat exited with code {exit}. Check the target mode, attack settings and backend device availability."
            };
            return new(exit, state, diagnostic, cancelled);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            HashcatRunner.TryKill(_process);
            return new(-1, JobState.Failed, "Hashcat process communication failed. Check backend availability and retry.");
        }
        finally
        {
            lock (_lifecycle)
            {
                _disposed = true;
                HashcatRunner.TryKill(_process); _process.Dispose(); _linked.Dispose(); _stop.Dispose();
            }
        }
    }

    private async Task ReadLinesAsync(StreamReader reader, bool isError)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (HashcatStatusParser.TryParse(line, out var status)) _progress?.Report(new("status", Status: status));
            else if (line.Contains('{') && line.Contains("\"status\"", StringComparison.Ordinal)) _progress?.Report(new("error", "A Hashcat status update could not be parsed."));
            else if (isError && !string.IsNullOrWhiteSpace(line)) _progress?.Report(new("error", SanitizeDiagnostic(line)));
            // Unstructured stdout may contain recovered plaintext or candidates. It is never forwarded to diagnostic logs.
        }
    }

    public static string SanitizeDiagnostic(string line)
    {
        if (line.Contains("Outdated or broken", StringComparison.OrdinalIgnoreCase)) return "Hashcat rejected an outdated or broken compute runtime. Install a supported driver/runtime for the selected device.";
        if (line.Contains("No devices", StringComparison.OrdinalIgnoreCase)) return "Hashcat could not find a usable backend device.";
        if (line.Contains("No hashes loaded", StringComparison.OrdinalIgnoreCase)) return "Hashcat did not load any hashes. Check the selected mode and target format.";
        if (line.Contains("Token length exception", StringComparison.OrdinalIgnoreCase)) return "A target line does not match the selected hash mode (token length).";
        if (line.Contains("Separator unmatched", StringComparison.OrdinalIgnoreCase)) return "A target line does not match the selected hash mode (separator).";
        if (line.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)) return "Hashcat encountered a file permission error.";
        if (line.Contains("CL_", StringComparison.Ordinal) || line.Contains("CUDA", StringComparison.OrdinalIgnoreCase)) return "The compute backend reported a driver or device error.";
        if (line.Contains("Insufficient", StringComparison.OrdinalIgnoreCase) && line.Contains("memory", StringComparison.OrdinalIgnoreCase)) return "The backend has insufficient available memory for this job.";
        return "Hashcat reported a diagnostic. Private target, path and plaintext data were excluded from the application log.";
    }
}

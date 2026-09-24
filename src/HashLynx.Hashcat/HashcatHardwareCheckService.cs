using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Hashcat;

public enum HardwareCheckOutcome { Passed, Failed, TimedOut, Cancelled }

public sealed record HardwareCheckResult(int DeviceId, HardwareCheckOutcome Outcome, string Message, TimeSpan Elapsed)
{
    public bool Passed => Outcome == HardwareCheckOutcome.Passed;
}

/// <summary>Verifies one selected device using a disposable known-answer recovery, independently of user jobs and potfiles.</summary>
public sealed class HashcatHardwareCheckService
{
    private readonly string _checkDirectory;
    private readonly HashcatRuntimeWorkspace _workspace;
    private readonly Func<HashcatCommand, CancellationToken, Task<ProcessCapture>> _capture;
    private readonly TimeSpan _timeout;

    public HashcatHardwareCheckService(string cacheDirectory,
        Func<HashcatCommand, CancellationToken, Task<ProcessCapture>>? capture = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _checkDirectory = Path.Combine(Path.GetFullPath(cacheDirectory), "hardware-checks");
        _workspace = new HashcatRuntimeWorkspace(cacheDirectory);
        _capture = capture ?? new HashcatRunner().CaptureAsync;
        _timeout = timeout ?? TimeSpan.FromSeconds(90);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public async Task<HardwareCheckResult> CheckAsync(HashcatInstallation installation, int deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (deviceId <= 0) throw new ArgumentOutOfRangeException(nameof(deviceId));
        var elapsed = Stopwatch.StartNew();
        var directory = Path.Combine(_checkDirectory, Guid.NewGuid().ToString("N"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        HardwareCheckResult Result(HardwareCheckOutcome outcome, string message) => new(deviceId, outcome, message, elapsed.Elapsed);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            // Always prepare our own working directory, even if the installation has not been probed yet.
            // Hashcat's caches and dictionary statistics must never be written into its configured release.
            var runtime = await _workspace.PrepareAsync(installation.ExecutablePath, deadline.Token).ConfigureAwait(false);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "sample.hashes");
            var words = Path.Combine(directory, "sample.txt");
            var output = Path.Combine(directory, "recovered.txt");
            var answer = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
            var answerBytes = Encoding.ASCII.GetBytes(answer);
            var hash = Convert.ToHexString(MD5.HashData(answerBytes)).ToLowerInvariant();
            await File.WriteAllTextAsync(target, hash + "\n", new UTF8Encoding(false), deadline.Token).ConfigureAwait(false);
            await File.WriteAllTextAsync(words, "HashLynxSample\n" + answer + "\n", new UTF8Encoding(false), deadline.Token).ConfigureAwait(false);
            var arguments = new string[]
            {
                "--hash-type", "0", "--attack-mode", "0", "--backend-devices", deviceId.ToString(CultureInfo.InvariantCulture),
                "--opencl-device-types", "1,2,3", "--workload-profile", "1", "--runtime", "30",
                "--session", "hashlynx-check-" + Path.GetFileName(directory), "--potfile-disable", "--restore-disable",
                "--logfile-disable", "--quiet", "--outfile", output, "--outfile-format", "1,3", "--", target, words
            };
            var capture = await _capture(new(installation.ExecutablePath, runtime, arguments), deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (capture.ExitCode == 0 && await ContainsAnswerAsync(output, hash, Convert.ToHexString(answerBytes), deadline.Token).ConfigureAwait(false))
                return Result(HardwareCheckOutcome.Passed, "Passed: this device recovered and verified the built-in sample. This checks basic recovery; support and speed can differ for other hash formats.");
            if (capture.ExitCode is 0 or 1)
                return Result(HardwareCheckOutcome.Failed, "Failed: the device did not return the expected sample recovery. Refresh devices and retry. Check the vendor driver/runtime or choose another device if this continues.");
            if (capture.ExitCode == 4)
                return Result(HardwareCheckOutcome.TimedOut, "The sample reached its runtime limit. Close other compute workloads and retry, or test another device.");
            return Result(HardwareCheckOutcome.Failed, FailureMessage(capture));
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? Result(HardwareCheckOutcome.Cancelled, "Hardware check cancelled. No recovery job was created.")
                : Result(HardwareCheckOutcome.TimedOut, "The device did not complete the sample within the time limit. First-time kernel compilation can take longer; retry once, then check the vendor driver/runtime or choose another device.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result(HardwareCheckOutcome.Failed, "The hardware check could not read or write its temporary files. Check the Hashcat installation and access to the application data folder, then retry.");
        }
        catch (HashcatException)
        {
            return Result(HardwareCheckOutcome.Failed, "The hardware check could not run Hashcat successfully. Validate the installation in Settings, refresh devices, and check the vendor driver/runtime.");
        }
        finally
        {
            // The directory is generated for this invocation only; no user-supplied files are placed here.
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* Only synthetic sample data can remain. */ }
        }
    }

    private static async Task<bool> ContainsAnswerAsync(string output, string hash, string hexAnswer, CancellationToken cancellationToken)
    {
        if (!File.Exists(output)) return false;
        await using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length > 4096) return false;
        using var reader = new StreamReader(stream);
        var contents = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return contents.Split('\n').Any(line => line.TrimEnd('\r').Equals(hash + ":" + hexAnswer, StringComparison.OrdinalIgnoreCase));
    }

    private static string FailureMessage(ProcessCapture capture)
    {
        const string generic = "Hashcat reported a diagnostic. Private target, path and plaintext data were excluded from the application log.";
        var diagnostics = (capture.StandardError + "\n" + capture.StandardOutput).Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line)).Select(HashcatRunningJob.SanitizeDiagnostic)
            .Where(message => message != generic).Distinct().Take(3).ToArray();
        return $"Failed: Hashcat exited with code {capture.ExitCode}. " + (diagnostics.Length > 0
            ? string.Join(" ", diagnostics)
            : "Refresh devices and validate the Hashcat installation. Check the vendor driver/runtime or choose another device.");
    }
}

using System.Diagnostics;
using System.Text;

namespace HashLynx.Extractors;

public sealed record ExtractorProcessRequest(string ExecutablePath, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);
public sealed record ExtractorProcessResult(int ExitCode, string StandardOutput, string StandardError, bool OutputTruncated = false);

public interface IExtractorProcessRunner
{
    Task<ExtractorProcessResult> RunAsync(ExtractorProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Launches only the configured executable, with separate arguments and bounded concurrent stream capture.</summary>
public sealed class ExtractorProcessRunner : IExtractorProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(ExtractorProcessRequest request)
    {
        if (!ToolLocator.IsSafeExecutable(request.ExecutablePath))
            throw new ArgumentException("Configure a native executable or a supported script interpreter, not a shell.", nameof(request));
        var info = new ProcessStartInfo(request.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.ExecutablePath) ?? AppContext.BaseDirectory
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        return info;
    }

    public async Task<ExtractorProcessResult> RunAsync(ExtractorProcessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = CreateStartInfo(request) };
        process.Start();
        var stdout = ReadBoundedAsync(process.StandardOutput, 16 * 1024 * 1024, cancellationToken);
        var stderr = ReadBoundedAsync(process.StandardError, 64 * 1024, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        return new(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
        {
            var remaining = maximumCharacters - output.Length;
            output.Append(buffer, 0, Math.Min(read, remaining));
            truncated |= read > remaining;
        }
        return (output.ToString(), truncated);
    }
}

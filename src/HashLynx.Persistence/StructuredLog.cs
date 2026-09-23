using System.Text.Json;

namespace HashLynx.Persistence;

/// <summary>Only application-authored summaries belong here; never pass process output, arguments, or targets.</summary>
public sealed class StructuredLog
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AppPaths paths;
    public StructuredLog(AppPaths paths) => this.paths = paths;

    public async Task WriteAsync(string eventName, string message, Exception? exception = null)
    {
        var entry = JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            eventName,
            message,
            errorType = exception?.GetType().Name,
            errorCode = exception?.HResult
        });
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(Path.Combine(paths.LogsDirectory, $"application-{DateTime.UtcNow:yyyy-MM-dd}.jsonl"), entry + Environment.NewLine).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}

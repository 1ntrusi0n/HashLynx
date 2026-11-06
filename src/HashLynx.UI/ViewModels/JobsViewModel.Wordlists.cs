using HashLynx.Core;
using HashLynx.Persistence;
using System.IO;

namespace HashLynx.UI.ViewModels;

public sealed partial class JobsViewModel
{
    private readonly JobWordlistStatisticsService _wordlistStatisticsService;
    private readonly CancellationTokenSource _wordlistLifetime;
    private readonly Dictionary<Guid, Task> _wordlistTasks = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _wordlistCancellations = [];

    public Task EnsureWordlistStatisticsAsync(JobViewModel job)
    {
        foreach (var completed in _wordlistTasks.Where(item => item.Value.IsCompleted).Select(item => item.Key).ToArray())
            _wordlistTasks.Remove(completed);
        if (!Jobs.Contains(job) || !job.HasWordlists || job.Record.WordlistStatistics is not null || !_services.CanStartWork)
            return Task.CompletedTask;
        if (_wordlistTasks.TryGetValue(job.Record.Id, out var existing) && !existing.IsCompleted) return existing;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_wordlistLifetime.Token);
        _wordlistCancellations[job.Record.Id] = cancellation;
        var task = CountWordlistsAsync(job, cancellation);
        _wordlistTasks[job.Record.Id] = task;
        return task;
    }

    private async Task CountWordlistsAsync(JobViewModel job, CancellationTokenSource cancellation)
    {
        try
        {
            using var operation = _services.BeginOperation();
            job.IsCountingWordlists = true;
            var counts = await _wordlistStatisticsService.InspectAsync(job.Record.Configuration, job.Record.InputSnapshots, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!Jobs.Contains(job)) return;
            job.Record.WordlistStatistics = counts.ToList();
            job.Refresh();
            await SaveAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // Source metadata is optional; a count/persistence failure must never interrupt a recovery process.
            job.Record.WordlistStatistics ??= job.Record.Configuration.Attack.Wordlists
                .Select(path => new JobWordlistInfo(path, null, null, null, "Line count unavailable. The recovery can continue; check access to the source wordlist.")).ToList();
            job.Refresh();
        }
        finally
        {
            job.IsCountingWordlists = false;
            _wordlistCancellations.Remove(job.Record.Id);
            cancellation.Dispose();
        }
    }
}

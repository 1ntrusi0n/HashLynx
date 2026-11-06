using System.Diagnostics;
using System.Globalization;

namespace HashLynx.Updates;

public static class ApplicationUpdateLauncher
{
    /// <summary>The returned waiting process may be terminated if application shutdown is declined or fails.</summary>
    public static Process Start(StagedApplicationUpdate staged, int parentProcessId)
    {
        var stage = Path.GetFullPath(staged.StageDirectory);
        var expectedHelper = Path.Combine(stage, "runner", "HashLynx.Updater.exe");
        var expectedPlan = Path.Combine(stage, "plan.json");
        if (!string.Equals(Path.GetFullPath(staged.HelperExecutablePath), expectedHelper, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(staged.PlanPath), expectedPlan, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The staged updater location is invalid.");
        UpdateFiles.EnsureNoReparsePoints(expectedHelper);
        UpdateFiles.EnsureNoReparsePoints(expectedPlan);
        if (!File.Exists(expectedHelper) || !File.Exists(expectedPlan)) throw new FileNotFoundException("The staged updater is incomplete. Download the update again.");
        var installation = ApplicationUpdateInstaller.GetInstallationDirectory(expectedPlan);
        var installed = UpdateFiles.ReadManifest(installation);
        var runner = Path.GetDirectoryName(expectedHelper)!;
        var helperFiles = installed.Files.Where(file => file.Path.StartsWith("Updater/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var declared = helperFiles.Select(file => file.Path[8..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in helperFiles)
        {
            var stagedPath = UpdateFiles.Child(runner, file.Path[8..]);
            var installedPath = UpdateFiles.Child(installation, file.Path);
            if (!File.Exists(stagedPath) || new FileInfo(stagedPath).Length != file.Length ||
                !string.Equals(UpdateFiles.HashAsync(stagedPath, CancellationToken.None).GetAwaiter().GetResult(), file.Sha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(UpdateFiles.HashAsync(installedPath, CancellationToken.None).GetAwaiter().GetResult(), file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The prepared updater changed after download. Prepare the update again.");
        }
        foreach (var path in Directory.EnumerateFiles(runner, "*", SearchOption.AllDirectories))
        {
            UpdateFiles.EnsureNoReparsePoints(path);
            if (!declared.Contains(Path.GetRelativePath(runner, path).Replace('\\', '/')))
                throw new InvalidDataException("The prepared updater folder contains unexpected files. Prepare the update again.");
        }
        var readyPath = Path.Combine(stage, "ready.txt");
        UpdateFiles.EnsureNoReparsePoints(readyPath);
        if (File.Exists(readyPath)) File.Delete(readyPath);
        using var parent = Process.GetProcessById(parentProcessId);
        var start = new ProcessStartInfo(expectedHelper)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(expectedHelper)!
        };
        start.ArgumentList.Add("--plan"); start.ArgumentList.Add(expectedPlan);
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(parentProcessId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--parent-start"); start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("The updater could not start. HashLynx was not changed.");
    }

    public static async Task WaitForReadyAsync(StagedApplicationUpdate staged, Process helper, CancellationToken cancellationToken = default)
    {
        var ready = Path.Combine(Path.GetFullPath(staged.StageDirectory), "ready.txt");
        var expected = helper.Id.ToString(CultureInfo.InvariantCulture);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (helper.HasExited) throw new InvalidOperationException("The updater could not initialize. HashLynx will remain open; install the .NET 10 Desktop Runtime or download a fresh release.");
                UpdateFiles.EnsureNoReparsePoints(ready);
                if (File.Exists(ready) && new FileInfo(ready).Length <= 32 && await File.ReadAllTextAsync(ready, timeout.Token).ConfigureAwait(false) == expected) return;
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("The updater did not become ready. HashLynx will remain open; try downloading the update again.", ex); }
    }
}

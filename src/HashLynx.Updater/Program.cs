using HashLynx.Updates;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace HashLynx.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--plan" || args[2] != "--parent-pid" || args[4] != "--parent-start" ||
            !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
            !long.TryParse(args[5], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return 2;
        string? installation = null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new InvalidOperationException("Close the elevated application and run HashLynx as a normal user before updating.");
            var planPath = Path.GetFullPath(args[1]);
            var stage = Path.GetDirectoryName(planPath)!;
            var expectedRunner = Path.Combine(stage, "runner");
            if (!Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar).Equals(expectedRunner, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The updater must run from the prepared staging folder.");
            installation = ApplicationUpdateInstaller.GetInstallationDirectory(planPath);
            ApplicationUpdateInstaller.ValidateInstallation(installation);
            var readyTemporary = Path.Combine(stage, "ready-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp");
            await using (var readyStream = new FileStream(readyTemporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var writer = new StreamWriter(readyStream))
                await writer.WriteAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            File.Move(readyTemporary, Path.Combine(stage, "ready.txt"), false);
            if (!await ApplicationUpdateInstaller.WaitForParentExitAsync(pid, ticks, TimeSpan.FromMinutes(2))) return 3;
            var result = await ApplicationUpdateInstaller.ApplyAsync(planPath);
            try { await File.WriteAllTextAsync(Path.Combine(stage, "result.json"), JsonSerializer.Serialize(result)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!result.Succeeded) ShowMessage(result.Message);
            if (result.Succeeded || result.RolledBack) Restart(installation);
            return result.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            // Never include paths, raw arguments, release content or process command lines in diagnostics.
            ShowMessage("HashLynx could not complete its update. Your local settings and recovery data were not changed. " +
                "Reopen HashLynx and try again, or download the application from its GitHub releases page.\n\n" + exception.GetType().Name);
            return 1;
        }
    }

    private static void Restart(string installation)
    {
        var executable = Path.Combine(installation, "HashLynx.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = installation };
        using var process = Process.Start(start);
    }

    private static void ShowMessage(string message) => MessageBoxW(nint.Zero, message, "HashLynx update", 0x00000010);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}

using HashLynx.Drives;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace HashLynx.DriveReader;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || !DriveProtocol.IsPipeName(args[0]) || !uint.TryParse(args[1], out var parent) || parent == 0) return 2;
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 3;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        // Even a stuck removable-device driver cannot leave a privileged reader running indefinitely.
        using var watchdog = new Timer(_ => Environment.Exit(8), null, TimeSpan.FromSeconds(95), Timeout.InfiniteTimeSpan);
        try
        {
            // Identification prevents a pipe server from impersonating the elevated reader.
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(lifetime.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != parent) return 4;
            var request = await DriveProtocol.ReadAsync<DriveReadRequest>(pipe, lifetime.Token);
            if (!DriveProtocol.IsVolumeId(request.VolumeId)) return 5;
            // No arbitrary filenames, offsets, writes, unlock calls or subprocesses are accepted.
            var disconnected = MonitorDisconnectAsync(pipe, lifetime);
            var result = await Task.Run(() => DriveMetadataReader.ReadAsync(request, lifetime.Token), lifetime.Token);
            await DriveProtocol.WriteAsync(pipe, result, lifetime.Token);
            lifetime.Cancel();
            try { await disconnected; } catch (OperationCanceledException) { }
            return 0;
        }
        catch { return 1; } // Never display or log target metadata or credentials.
    }
    private static async Task MonitorDisconnectAsync(Stream pipe, CancellationTokenSource lifetime)
    {
        try { var count = await pipe.ReadAsync(new byte[1], lifetime.Token); if (count is 0 or 1) Environment.Exit(6); }
        finally { lifetime.Cancel(); }
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
}

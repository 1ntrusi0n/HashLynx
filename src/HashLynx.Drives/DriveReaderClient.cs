using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace HashLynx.Drives;

public sealed class DriveReaderClient(string? helperPath = null) : IBitLockerDriveService
{
    public Task<IReadOnlyList<DriveCandidate>> DiscoverAsync(CancellationToken ct = default) => Task.Run(() => WindowsVolumes.Discover(ct), ct);
    public async Task<DriveReadResponse> ExtractAsync(DriveCandidate candidate, CancellationToken ct = default)
    {
        if (!DriveProtocol.IsVolumeId(candidate.VolumeId)) throw new InvalidOperationException("Select a valid local drive first.");
        var helper = Path.GetFullPath(helperPath ?? Path.Combine(AppContext.BaseDirectory, "DriveReader", "HashLynx.DriveReader.exe"));
        if (!File.Exists(helper)) throw new InvalidOperationException("The drive reader is missing. Use the complete application folder, including DriveReader.");
        // Revalidate the mount point before elevation; the helper then opens the stable volume ID.
        var current = await DiscoverAsync(ct);
        if (!current.Any(item => item.VolumeId == candidate.VolumeId && item.MountPoint == candidate.MountPoint))
            throw new InvalidOperationException("The selected drive changed or was disconnected. Refresh and select it again.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var pipeName = "HashLynx.DriveReader." + Guid.NewGuid().ToString("N");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Explicit same-user ACL permits the user's elevated token; the PID check rejects other clients.
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
        using var process = await Task.Run(() => Launch(helper, pipeName), CancellationToken.None);
        try
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid != process.Id)
                throw new InvalidOperationException("The drive reader connection could not be verified.");
            await DriveProtocol.WriteAsync(pipe, new DriveReadRequest(candidate.VolumeId), timeout.Token);
            return await DriveProtocol.ReadAsync<DriveReadResponse>(pipe, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new InvalidOperationException("The drive reader timed out. Refresh the drive list and retry."); }
    }
    private static Process Launch(string path, string pipe)
    {
        // The sole UAC exception: fixed executable plus validated pipe name and numeric PID, never a command shell.
        if (!DriveProtocol.IsPipeName(pipe)) throw new InvalidOperationException("Invalid drive reader connection.");
        var info = new ShellExecuteInfo { Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x40 | 0x100, Verb = "runas", File = path,
            Parameters = pipe + " " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), Directory = Path.GetDirectoryName(path), Show = 0 };
        if (!ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(error == 1223 ? "Administrator permission was cancelled. The drive was not read." : $"Windows could not start the drive reader (error {error}).");
        }
        using var handle = new SafeProcessHandle(info.Process, true);
        return Process.GetProcessById(checked((int)GetProcessId(handle)));
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size; public uint Mask; public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? File;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Parameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Directory;
        public int Show; public IntPtr Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey; public uint HotKey; public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);
    [DllImport("kernel32.dll")]
    private static extern uint GetProcessId(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
}

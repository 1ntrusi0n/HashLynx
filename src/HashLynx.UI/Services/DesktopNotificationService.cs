using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HashLynx.UI.Services;

/// <summary>Transient Windows notification-area balloon. Text is fixed status text, never target or password data.</summary>
public sealed class DesktopNotificationService : IDisposable
{
    private const int CallbackMessage = 0x8000 + 87;
    private HwndSource? _source;
    private Action? _clicked;
    private readonly DispatcherTimer _expiry = new() { Interval = TimeSpan.FromSeconds(20) };
    private bool _added;

    public DesktopNotificationService() => _expiry.Tick += (_, _) => Remove();
    public bool Show(bool recovered, Action clicked)
    {
        var window = Application.Current?.MainWindow;
        if (window is null) return false;
        if (_source is null)
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            if (_source is null) return false;
            _source.AddHook(Hook);
        }
        Remove(); _clicked = clicked;
        var data = Data();
        data.Flags = 1 | 2 | 4 | 16; data.CallbackMessage = CallbackMessage;
        data.Icon = LoadIconW(IntPtr.Zero, new IntPtr(32516));
        data.Tip = "HashLynx";
        data.InfoTitle = recovered ? "HashLynx: recovery results available" : "HashLynx: attempt finished";
        data.Info = "Open HashLynx to review the session. Passwords are shown only inside the app.";
        data.InfoFlags = 1;
        _added = Shell_NotifyIconW(0, ref data);
        if (_added) _expiry.Start();
        return _added;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == CallbackMessage && (lParam.ToInt64() & 0xffff) is 0x405 or 0x202)
        {
            var action = _clicked; Remove();
            if (Application.Current.MainWindow is { } window) { if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal; window.Activate(); }
            action?.Invoke(); handled = true;
        }
        return IntPtr.Zero;
    }
    private NotifyIconData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _source?.Handle ?? IntPtr.Zero, Id = 1, Tip = "", Info = "", InfoTitle = "" };
    private void Remove() { _expiry.Stop(); if (_added) { var data = Data(); Shell_NotifyIconW(2, ref data); _added = false; } _clicked = null; }
    public void Dispose() { Remove(); _source?.RemoveHook(Hook); _source = null; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public IntPtr Window; public uint Id; public uint Flags; public uint CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State; public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid GuidItem; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
}

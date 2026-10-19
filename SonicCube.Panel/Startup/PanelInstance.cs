using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SonicCube.Panel.Startup;

/// <summary>按真实/模拟模式协调单实例,通过主窗口标记恢复已有实例。</summary>
public sealed class PanelInstance : IDisposable
{
    internal const string InstanceRoot = @"Local\SonicCube.Panel.SingleInstance";
    private const int RestoreWindow = 9;
    private readonly Mutex _mutex;
    private readonly string _windowPropertyName;
    private Window? _window;
    private IntPtr _windowHandle;
    private bool _disposed;

    public PanelInstance(bool simulate) : this(simulate, InstanceRoot) { }

    internal PanelInstance(bool simulate, string instanceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        _windowPropertyName = GetWindowPropertyName(simulate, instanceRoot);
        _mutex = new Mutex(initiallyOwned: true, GetMutexName(simulate, instanceRoot), out bool createdNew);
        IsOwner = createdNew;
    }

    public bool IsOwner { get; }

    internal static string GetMutexName(bool simulate, string instanceRoot = InstanceRoot) =>
        instanceRoot + (simulate ? ".Simulated" : "");

    internal static string GetWindowPropertyName(bool simulate, string instanceRoot = InstanceRoot) =>
        GetMutexName(simulate, instanceRoot) + ".Window";

    public void RegisterWindow(Window window)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        if (!IsOwner) throw new InvalidOperationException("Only the owning instance can register a window.");
        if (ReferenceEquals(_window, window)) return;

        UnregisterWindow();
        _window = window;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnWindowClosed;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) MarkWindow(handle);
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        MarkWindow(new WindowInteropHelper(_window!).Handle);

    private void MarkWindow(IntPtr handle)
    {
        if (!SetProp(handle, _windowPropertyName, new IntPtr(1)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        _windowHandle = handle;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => UnregisterWindow();

    private void UnregisterWindow()
    {
        if (_window is not null)
        {
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnWindowClosed;
            _window = null;
        }
        if (_windowHandle != IntPtr.Zero)
        {
            RemoveProp(_windowHandle, _windowPropertyName);
            _windowHandle = IntPtr.Zero;
        }
    }

    /// <summary>找到同模式窗口并请求恢复/激活;最多等待约两秒让首实例完成初始化。</summary>
    public async Task<bool> ActivateExistingAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int attempt = 0; attempt <= 20 && !_disposed; attempt++)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((handle, _) =>
            {
                if (GetProp(handle, _windowPropertyName) == IntPtr.Zero) return true;
                found = handle;
                return false;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                ShowWindowAsync(found, RestoreWindow);
                var popup = GetLastActivePopup(found);
                SetForegroundWindow(popup != IntPtr.Zero && IsWindowVisible(popup) ? popup : found);
                return true;
            }
            if (attempt < 20) await Task.Delay(100);
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterWindow();
        try
        {
            if (IsOwner) _mutex.ReleaseMutex();
        }
        finally { _mutex.Dispose(); }
    }

    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProp(IntPtr handle, string name, IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetProp(IntPtr handle, string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr handle, string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetLastActivePopup(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr handle);
}

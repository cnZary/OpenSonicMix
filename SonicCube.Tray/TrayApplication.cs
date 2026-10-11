using SonicCube.Tray.Device;
using SonicCube.Tray.Native;
using SonicCube.Tray.Settings;

namespace SonicCube.Tray;

/// <summary>
/// Win32 原生托盘窗口(见需求 4.2)。
/// 隐藏消息窗口 + 长期托盘图标 + 消息循环;左键打开面板,右键快速菜单;
/// 处理 Explorer 重启(TaskbarCreated)恢复图标、电源广播、设备变化通知。
/// 消息循环不被 MIDI 操作阻塞(所有设备操作走异步命令协调器)。
/// </summary>
internal sealed class TrayApplication : IDisposable
{
    private const uint WmTrayCallback = Win32.WM_USER + 1;
    private const uint TrayIconId = 1;

    // 菜单命令 ID
    private const int Cmd48V = 1001;
    private const int CmdEfx = 1002;
    private const int CmdMonitor = 1003;
    private const int CmdOnAir = 1004;
    private const int CmdMasterMute = 1005;
    private const int CmdMono = 1006;
    private const int CmdModeLow = 1010;
    private const int CmdModeFast = 1011;
    private const int CmdOpenPanel = 1020;
    private const int CmdExit = 1021;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarCreatedMsg;
    private Win32.WndProcDelegate? _wndProc; // 保持委托存活
    private bool _iconAdded;

    private readonly DeviceCoordinator _coordinator;
    private readonly WorkModeManager _modeManager;
    private readonly SettingsStore _settings;
    private readonly Ipc.IpcServer _ipc;

    public TrayApplication(DeviceCoordinator coordinator, WorkModeManager modeManager, SettingsStore settings, Ipc.IpcServer ipc)
    {
        _coordinator = coordinator;
        _modeManager = modeManager;
        _settings = settings;
        _ipc = ipc;
    }

    public unsafe int Run()
    {
        _taskbarCreatedMsg = Win32.RegisterWindowMessage("TaskbarCreated");

        _wndProc = WndProc;
        IntPtr hInstance = Win32.GetModuleHandle(null);
        var wc = new Win32.WNDCLASSEXW
        {
            cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
            lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = AllocString("SonicCubeTrayWnd"),
        };
        Win32.RegisterClassEx(&wc);

        _hwnd = Win32.CreateWindowEx(0, "SonicCubeTrayWnd", "SonicCubeTray",
            0, 0, 0, 0, 0, Win32.HWND_MESSAGE == -3 ? new IntPtr(-3) : IntPtr.Zero,
            IntPtr.Zero, hInstance, IntPtr.Zero);

        _hIcon = LoadTrayIcon();
        AddTrayIcon();

        // 消息循环
        Win32.MSG msg;
        int r;
        while ((r = Win32.GetMessage(&msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (r == -1) break;
            Win32.TranslateMessage(&msg);
            Win32.DispatchMessage(&msg);
        }
        return 0;
    }

    private static IntPtr AllocString(string s)
    {
        var ptr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(s);
        _allocated.Add(ptr);
        return ptr;
    }
    private static readonly List<IntPtr> _allocated = new();

    private IntPtr LoadTrayIcon()
    {
        // 优先尝试 exe 内嵌图标,否则用系统应用图标。
        IntPtr h = Win32.LoadImage(Win32.GetModuleHandle(null), null, Win32.IMAGE_ICON, 0, 0, 0);
        if (h == IntPtr.Zero)
            h = Win32.LoadIcon(IntPtr.Zero, Win32.IDI_APPLICATION);
        return h;
    }

    private unsafe void AddTrayIcon()
    {
        var nid = BuildNotifyIcon();
        if (Win32.Shell_NotifyIcon(Win32.NIM_ADD, &nid))
        {
            _iconAdded = true;
            nid.uVersion = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, &nid);
        }
    }

    private unsafe void RemoveTrayIcon()
    {
        if (!_iconAdded) return;
        var nid = BuildNotifyIcon();
        Win32.Shell_NotifyIcon(Win32.NIM_DELETE, &nid);
        _iconAdded = false;
    }

    private Win32.NOTIFYICONDATAW BuildNotifyIcon()
    {
        var nid = new Win32.NOTIFYICONDATAW
        {
            cbSize = NotifyIconDataSize,
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP,
            uCallbackMessage = WmTrayCallback,
            hIcon = _hIcon,
        };
        SetTipText(ref nid, Localization.TrayStrings.Get(Localization.TrayStrings.Key.AppTitle));
        return nid;
    }

    private static unsafe readonly uint NotifyIconDataSize = (uint)sizeof(Win32.NOTIFYICONDATAW);

    private static unsafe void SetTipText(ref Win32.NOTIFYICONDATAW nid, string tip)
    {
        Win32.NOTIFYICONDATAW* p = (Win32.NOTIFYICONDATAW*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref nid);
        int n = Math.Min(tip.Length, 127);
        for (int i = 0; i < n; i++) p->szTip[i] = tip[i];
        p->szTip[n] = '\0';
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == _taskbarCreatedMsg)
        {
            // Explorer 重启:恢复托盘图标。
            _iconAdded = false;
            AddTrayIcon();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmTrayCallback:
                HandleTrayCallback(lParam);
                return IntPtr.Zero;

            case Win32.WM_COMMAND:
                HandleCommand(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;

            case Win32.WM_POWERBROADCAST:
                HandlePowerBroadcast(wParam);
                return (IntPtr)1; // TRUE

            case Win32.WM_DEVICECHANGE:
                // 设备变化:快速连接模式的重连由 MonitorLoop 低频扫描兜底;
                // 此处仅记录,避免在消息循环里做重活。
                Logging.Logger.Debug("Tray", $"WM_DEVICECHANGE wParam=0x{wParam.ToInt64():X}");
                return (IntPtr)1;

            case Win32.WM_DESTROY:
                RemoveTrayIcon();
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleTrayCallback(IntPtr lParam)
    {
        uint mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
        switch (mouseMsg)
        {
            case Win32.WM_LBUTTONUP:
            case Win32.WM_LBUTTONDBLCLK:
                OpenPanel();
                break;
            case Win32.WM_RBUTTONUP:
            case Win32.WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    // ── 快速菜单 ────────────────────────────────────────────

    private void ShowContextMenu()
    {
        var menu = Win32.CreatePopupMenu();

        // 六个快速开关:已连接=实时勾选;低占用空闲=断开前最后状态;未连接过=全不勾。
        var sw = _coordinator.GetSwitchStates();
        void AddSwitch(int cmdId, Localization.TrayStrings.Key key, int state)
        {
            uint flags = Win32.MF_STRING;
            if (sw.HasData && state == 1)
                flags |= (uint)Win32.MF_CHECKED;
            else
                flags |= (uint)Win32.MF_UNCHECKED;
            Win32.AppendMenu(menu, flags, (IntPtr)cmdId, S(key));
        }
        AddSwitch(Cmd48V, Localization.TrayStrings.Key.Switch48V, sw.Phantom48V);
        AddSwitch(CmdEfx, Localization.TrayStrings.Key.SwitchEFX, sw.Efx);
        AddSwitch(CmdMonitor, Localization.TrayStrings.Key.SwitchMonitor, sw.Monitor);
        AddSwitch(CmdOnAir, Localization.TrayStrings.Key.SwitchOnAir, sw.OnAir);
        AddSwitch(CmdMasterMute, Localization.TrayStrings.Key.SwitchMasterMute, sw.MasterMute);
        AddSwitch(CmdMono, Localization.TrayStrings.Key.SwitchMono, sw.MonoSw);
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);

        // 工作模式子菜单(单选)。
        var modeMenu = Win32.CreatePopupMenu();
        var mode = _modeManager.CurrentMode;
        uint lowFlags = Win32.MF_STRING | (uint)(mode == WorkMode.LowResource ? Win32.MF_CHECKED : Win32.MF_UNCHECKED);
        uint fastFlags = Win32.MF_STRING | (uint)(mode == WorkMode.FastConnection ? Win32.MF_CHECKED : Win32.MF_UNCHECKED);
        Win32.AppendMenu(modeMenu, lowFlags | Win32.MF_RADIOCHECK, (IntPtr)CmdModeLow, S(Localization.TrayStrings.Key.ModeLowResource));
        Win32.AppendMenu(modeMenu, fastFlags | Win32.MF_RADIOCHECK, (IntPtr)CmdModeFast, S(Localization.TrayStrings.Key.ModeFastConnection));
        Win32.AppendMenu(menu, Win32.MF_STRING | Win32.MF_POPUP, modeMenu, S(Localization.TrayStrings.Key.WorkMode));
        Win32.AppendMenu(menu, Win32.MF_SEPARATOR, IntPtr.Zero, null);

        Win32.AppendMenu(menu, Win32.MF_STRING, (IntPtr)CmdOpenPanel, S(Localization.TrayStrings.Key.OpenPanel));
        Win32.AppendMenu(menu, Win32.MF_STRING, (IntPtr)CmdExit, S(Localization.TrayStrings.Key.Exit));

        // 菜单需要前台窗口才能正确收回焦点。
        Win32.SetForegroundWindow(_hwnd);
        Win32.GetCursorPos(out var pt);
        int cmd = Win32.TrackPopupMenuEx(menu,
            Win32.TPM_RETURNCMD | Win32.TPM_NONOTIFY | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN,
            pt.x, pt.y, _hwnd, IntPtr.Zero);
        Win32.DestroyMenu(menu);
        Win32.PostMessage(_hwnd, Win32.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        if (cmd != 0) HandleCommand(cmd);
    }

    private static string S(Localization.TrayStrings.Key k) => Localization.TrayStrings.Get(k);

    private void HandleCommand(int cmd)
    {
        switch (cmd)
        {
            case Cmd48V: ToggleSwitch(QuickSwitch.Phantom48V); break;
            case CmdEfx: ToggleSwitch(QuickSwitch.Efx); break;
            case CmdMonitor: ToggleSwitch(QuickSwitch.Monitor); break;
            case CmdOnAir: ToggleSwitch(QuickSwitch.OnAir); break;
            case CmdMasterMute: ToggleSwitch(QuickSwitch.MasterMute); break;
            case CmdMono: ToggleSwitch(QuickSwitch.Mono); break;
            case CmdModeLow: _modeManager.SetMode(WorkMode.LowResource); break;
            case CmdModeFast: _modeManager.SetMode(WorkMode.FastConnection); break;
            case CmdOpenPanel: OpenPanel(); break;
            case CmdExit: RequestExit(); break;
        }
    }

    /// <summary>快速开关:立即响应菜单,操作进异步队列,失败给通知。</summary>
    private void ToggleSwitch(QuickSwitch sw)
    {
        _ = Task.Run(async () =>
        {
            var r = await _coordinator.ToggleSwitchAsync(sw, ActionSource.ManualTray);
            _modeManager.NotifyUserActivity();
            if (!r.Success && r.ErrorCode != "busy")
            {
                string title = Localization.TrayStrings.Get(Localization.TrayStrings.Key.ErrToggleFailed);
                string body = r.ErrorCode switch
                {
                    "open_failed" => Localization.TrayStrings.Get(Localization.TrayStrings.Key.ErrOpenFailedHint),
                    "timeout" => Localization.TrayStrings.Get(Localization.TrayStrings.Key.ErrTimeout),
                    _ => r.ErrorDetail ?? r.ErrorCode ?? "",
                };
                ShowBalloon(title, body);
            }
        });
    }

    private unsafe void ShowBalloon(string title, string body)
    {
        var nid = BuildNotifyIcon();
        nid.uFlags = Win32.NIF_INFO;
        Win32.NOTIFYICONDATAW* p = (Win32.NOTIFYICONDATAW*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref nid);
        int tn = Math.Min(title.Length, 63);
        for (int i = 0; i < tn; i++) p->szInfoTitle[i] = title[i];
        p->szInfoTitle[tn] = '\0';
        int bn = Math.Min(body.Length, 255);
        for (int i = 0; i < bn; i++) p->szInfo[i] = body[i];
        p->szInfo[bn] = '\0';
        nid.dwInfoFlags = 0x3; // NIIF_WARNING
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, &nid);
    }

    // ── Panel 启动 ──────────────────────────────────────────

    private void OpenPanel()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var panelPath = Path.Combine(exeDir, "SonicCube.Panel.exe");
            if (!File.Exists(panelPath))
            {
                Logging.Logger.Warn("Tray", $"未找到 Panel: {panelPath}");
                return;
            }
            // 若已有 Panel 实例,激活它(由 Panel 端 Mutex/IPC 处理);否则启动。
            var psi = new System.Diagnostics.ProcessStartInfo(panelPath) { UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
            Logging.Logger.Info("Tray", "已请求打开完整面板");
        }
        catch (Exception ex)
        {
            Logging.Logger.Error("Tray", $"启动 Panel 失败: {ex.Message}");
        }
    }

    private void HandlePowerBroadcast(IntPtr wParam)
    {
        int evt = wParam.ToInt32();
        switch (evt)
        {
            case Win32.PBT_APMSUSPEND:
                Logging.Logger.Info("Tray", "系统休眠");
                // 休眠:MIDI 句柄将失效,但不在此处做重活;唤醒时重评估。
                break;
            case Win32.PBT_APMRESUMEAUTOMATIC:
            case Win32.PBT_APMRESUMESUSPEND:
                Logging.Logger.Info("Tray", "系统唤醒,重新评估设备连接");
                // 不能仅因唤醒就重开 48V;交给快速模式的重连状态机按可信离线→在线处理。
                if (_modeManager.CurrentMode == WorkMode.FastConnection)
                    _ = Task.Run(async () => await _coordinator.DisconnectAsync("系统唤醒后重评估"));
                break;
        }
    }

    private void RequestExit()
    {
        Logging.Logger.Info("Tray", "退出托盘");
        _modeManager.Shutdown();
        _ipc.Stop();
        _coordinator.Shutdown();
        RemoveTrayIcon();
        Win32.DestroyWindow(_hwnd);
    }

    public void Dispose()
    {
        RemoveTrayIcon();
        foreach (var p in _allocated)
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p);
    }
}

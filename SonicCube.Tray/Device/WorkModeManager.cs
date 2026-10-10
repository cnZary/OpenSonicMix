using SonicCube.Tray.Device;
using SonicCube.Tray.Settings;

namespace SonicCube.Tray.Device;

/// <summary>
/// 工作模式管理器(见需求第五、六节)。
/// 低占用模式:按需连接 + 60 秒空闲释放 + 启动自动动作,不监听重连、不自动重连。
/// 快速连接模式:保持连接 + 断线自动恢复 + 可选的重连自动动作。
/// 所有逻辑通过 DeviceCoordinator 串行化,不阻塞 Win32 消息循环。
/// </summary>
public sealed class WorkModeManager
{
    private readonly DeviceCoordinator _coordinator;
    private readonly SettingsStore _settings;

    private readonly object _sync = new();
    private CancellationTokenSource? _idleCts;
    private CancellationTokenSource? _lifecycleCts; // 重连扫描 / 启动动作等待
    private long _lastHandledReconnectEpoch = -1;
    private bool _startupActionsDone;   // 本进程周期内初始启动动作只执行一次
    private bool _reconnectPendingInitial; // 快速模式启动时设备不在,首次上线是否计入启动窗口

    public WorkMode CurrentMode => _settings.Current.Mode;

    public WorkModeManager(DeviceCoordinator coordinator, SettingsStore settings)
    {
        _coordinator = coordinator;
        _settings = settings;
        _coordinator.Connected += OnConnected;
        _coordinator.Disconnected += OnDisconnected;
        _coordinator.CommandCompleted += OnCommandCompleted;
    }

    /// <summary>启动(Windows 登录或手动):读取配置,决定是否执行启动自动动作。</summary>
    public void Start()
    {
        var s = _settings.Current;
        bool hasStartupAction = s.Startup.Enable48V || s.Startup.EnableEFX;

        if (CurrentMode == WorkMode.FastConnection)
        {
            // 快速连接模式:持续保持连接;启动动作在首次建连后执行。
            _reconnectPendingInitial = true;
            StartFastConnectionDiscovery();
        }
        else
        {
            // 低占用模式:仅在有启动动作时按需连接执行。
            if (hasStartupAction)
                RunStartupActionsWithRetry();
        }
    }

    // ── 启动自动动作 ────────────────────────────────────────

    /// <summary>低占用模式:带 5 分钟窗口与 2 秒重试的启动自动动作。</summary>
    private void RunStartupActionsWithRetry()
    {
        CancelLifecycle();
        _lifecycleCts = new CancellationTokenSource();
        var ct = _lifecycleCts.Token;
        Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(5);
            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                var c = await _coordinator.EnsureConnectedAsync(ActionSource.Startup, TimeSpan.FromSeconds(10));
                if (c.Success)
                {
                    await ExecuteStartupActionsOnceAsync(ActionSource.Startup);
                    return;
                }
                try { await Task.Delay(2000, ct); } catch { return; }
            }
            Logging.Logger.Warn("Auto", "启动自动动作等待设备超时(5 分钟)");
        });
    }

    /// <summary>执行已启用的启动动作(每个进程周期仅一次;48V 需授权)。</summary>
    private async Task ExecuteStartupActionsOnceAsync(ActionSource source)
    {
        if (_startupActionsDone && source == ActionSource.Startup) return;
        if (source == ActionSource.Startup) _startupActionsDone = true;
        await ExecuteStartupActionsAsync(source);
    }

    /// <summary>核心:读取当前状态,对未开启且已授权的目标写入 1 并验证回包。</summary>
    private async Task ExecuteStartupActionsAsync(ActionSource source)
    {
        var s = _settings.Current.Startup;
        var session = _coordinator.Session;
        if (session == null || !_coordinator.IsConnected)
        {
            Logging.Logger.Warn("Auto", $"执行启动动作时未连接 (source={source})");
            return;
        }

        // EFX
        if (s.EnableEFX)
        {
            if (session.Global.Efx == 1)
                Logging.Logger.Info("Auto", $"EFX 已开启,跳过 (source={source})");
            else
            {
                var r = await _coordinator.SetParameterAsync(Protocol.Commands.CommandAddress.Efx, 1, source);
                Logging.Logger.Info("Auto", $"EFX 自动开启 (source={source}) → {(r.Success ? "成功" : r.ErrorCode)}");
            }
        }

        // 48V(需授权)
        if (s.Enable48V)
        {
            if (!s.PhantomPowerConsent)
                Logging.Logger.Warn("Auto", $"48V 自动开启已启用但未获用户授权,跳过 (source={source})");
            else if (session.Global.Phantom48V == 1)
                Logging.Logger.Info("Auto", $"48V 已开启,跳过 (source={source})");
            else
            {
                var r = await _coordinator.SetParameterAsync(Protocol.Commands.CommandAddress.Phantom48V, 1, source);
                Logging.Logger.Info("Auto", $"48V 自动开启 (source={source}) → {(r.Success ? "成功" : r.ErrorCode)}");
            }
        }
    }

    // ── 快速连接模式发现 / 重连 ─────────────────────────────

    private void StartFastConnectionDiscovery()
    {
        CancelLifecycle();
        _lifecycleCts = new CancellationTokenSource();
        var ct = _lifecycleCts.Token;
        Task.Run(async () =>
        {
            // 初始:尝试连接;成功则执行启动动作(计入启动窗口)。
            var c = await _coordinator.EnsureConnectedAsync(ActionSource.Startup, TimeSpan.FromSeconds(10));
            if (c.Success)
            {
                await ExecuteStartupActionsOnceAsync(ActionSource.Startup);
                _reconnectPendingInitial = false;
            }
            // 进入持续监控(重连循环由 OnDisconnected 驱动 + 低频扫描)。
            _ = MonitorLoopAsync(ct);
        });
    }

    /// <summary>快速连接模式:断线后低频重扫(2s),发现设备即重连。</summary>
    private async Task MonitorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && CurrentMode == WorkMode.FastConnection)
        {
            try { await Task.Delay(2000, ct); } catch { return; }
            if (CurrentMode != WorkMode.FastConnection) return;

            // 存活检测:已连接但端口已从系统消失 = 物理拔出(WinMM 不主动通知 SysEx 端口消失)。
            // 先走正常断开流程(SetState + Disconnected 事件),再由下方重连逻辑接管。
            if (_coordinator.IsConnected && !_coordinator.IsConnectionAlive())
            {
                Logging.Logger.Info("Device", "快速连接模式:检测到设备端口消失,断开并重连");
                await _coordinator.DisconnectAsync("设备端口消失");
            }

            if (!_coordinator.IsConnected && !_coordinator.HasPendingWork)
            {
                // 检查端口是否还在(端口消失 = 设备掉线);若掉线则重连。
                bool devicePresent = Shared.Midi.MidiDevice.EnumerateInputs().Count > 0
                                  && Shared.Midi.MidiDevice.EnumerateOutputs().Count > 0;
                if (devicePresent && _coordinator.State == ConnectionState.Disconnected)
                {
                    Logging.Logger.Info("Device", "快速连接模式:检测到设备,尝试重连");
                    var c = await _coordinator.EnsureConnectedAsync(ActionSource.DeviceReconnect, TimeSpan.FromSeconds(10));
                    if (c.Success) OnReconnected();
                }
            }
        }
    }

    /// <summary>可信「离线→在线」转换后的处理:状态同步已在握手内完成;按需执行重连自动动作。</summary>
    private void OnReconnected()
    {
        var s = _settings.Current;
        long epoch = _coordinator.ConnectEpoch;

        // 每个「设备上线事件」最多执行一次自动动作。
        if (epoch == _lastHandledReconnectEpoch) return;
        _lastHandledReconnectEpoch = epoch;

        // 若本次上线属于启动窗口内的首次连接,启动动作已由 StartFastConnectionDiscovery 执行,不重复。
        if (_reconnectPendingInitial)
        {
            _reconnectPendingInitial = false;
            return;
        }

        if (s.Mode == WorkMode.FastConnection && s.Startup.ReapplyOnReconnect)
        {
            Logging.Logger.Info("Auto", $"设备重连(epoch={epoch}),执行重连自动动作");
            _ = ExecuteStartupActionsAsync(ActionSource.DeviceReconnect);
        }
        else
        {
            Logging.Logger.Info("Auto", $"设备重连(epoch={epoch}),重连自动动作已关闭,仅恢复连接与状态");
        }
    }

    private void OnConnected() { /* 状态已由握手同步 */ }

    private void OnDisconnected()
    {
        // 快速连接模式下的物理掉线:MonitorLoop 负责重新发现。
        // 此处无需动作,仅确保不会误判为「重连事件」。
    }

    // ── 60 秒空闲释放(低占用模式)──────────────────────────

    private void OnCommandCompleted()
    {
        if (CurrentMode != WorkMode.LowResource) return;
        ScheduleIdleDisconnect();
    }

    /// <summary>重置并开始空闲倒计时。</summary>
    public void ScheduleIdleDisconnect()
    {
        if (CurrentMode != WorkMode.LowResource) return;
        CancelIdle();
        var cts = new CancellationTokenSource();
        lock (_sync) _idleCts = cts;
        int seconds = Math.Max(5, _settings.Current.IdleDisconnectSeconds);
        _ = IdleCountdownAsync(seconds, cts.Token);
    }

    private async Task IdleCountdownAsync(int seconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
            // 复查:仍有命令、Panel 打开、模式已切换则放弃断开。
            if (ct.IsCancellationRequested) return;
            if (CurrentMode != WorkMode.LowResource) return;
            if (_coordinator.PanelOpen) { ScheduleIdleDisconnect(); return; }
            if (_coordinator.HasPendingWork) { ScheduleIdleDisconnect(); return; }
            Logging.Logger.Info("Device", "低占用模式空闲超时,释放 MIDI 端口");
            await _coordinator.DisconnectAsync("低占用模式空闲超时");
        }
        catch (OperationCanceledException) { }
    }

    private void CancelIdle()
    {
        lock (_sync)
        {
            _idleCts?.Cancel();
            _idleCts?.Dispose();
            _idleCts = null;
        }
    }

    private void CancelLifecycle()
    {
        lock (_sync)
        {
            _lifecycleCts?.Cancel();
            _lifecycleCts?.Dispose();
            _lifecycleCts = null;
        }
    }

    // ── 模式切换 ────────────────────────────────────────────

    /// <summary>动态切换工作模式(见 5.5),不丢命令,不触发自动动作。</summary>
    public void SetMode(WorkMode mode)
    {
        var old = CurrentMode;
        if (old == mode) return;
        _settings.Current.Mode = mode;
        _settings.Save();
        Logging.Logger.Info("Mode", $"工作模式切换: {old} → {mode}");

        if (mode == WorkMode.FastConnection)
        {
            CancelIdle();
            // 不执行自动动作;仅建立/保持连接并启用断线恢复。
            _ = _coordinator.EnsureConnectedAsync(ActionSource.ManualTray, TimeSpan.FromSeconds(10));
            RestartFastMonitor();
        }
        else // → 低占用
        {
            CancelLifecycle();
            // 不立即中断正在执行的命令;若无 Panel 且无命令则开始空闲倒计时。
            if (!_coordinator.PanelOpen)
                ScheduleIdleDisconnect();
        }
    }

    private void RestartFastMonitor()
    {
        CancelLifecycle();
        _lifecycleCts = new CancellationTokenSource();
        _ = MonitorLoopAsync(_lifecycleCts.Token);
    }

    /// <summary>Panel 打开:取消空闲断开。</summary>
    public void NotifyPanelOpened()
    {
        _coordinator.PanelOpen = true;
        CancelIdle();
        // 确保连接以便 Panel 同步(作为独立 op,不在工作线程内嵌套入队)。
        _ = _coordinator.EnsureConnectedAsync(ActionSource.ManualPanel, TimeSpan.FromSeconds(10));
    }

    /// <summary>Panel 关闭:低占用模式开始空闲倒计时。</summary>
    public void NotifyPanelClosed()
    {
        _coordinator.PanelOpen = false;
        if (CurrentMode == WorkMode.LowResource)
            ScheduleIdleDisconnect();
    }

    /// <summary>用户手动操作后调用:重置空闲计时(低占用模式)。</summary>
    public void NotifyUserActivity()
    {
        if (CurrentMode == WorkMode.LowResource)
            ScheduleIdleDisconnect();
    }

    public void Shutdown()
    {
        CancelIdle();
        CancelLifecycle();
    }
}

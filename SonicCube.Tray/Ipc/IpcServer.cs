using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using SonicCube.Protocol.Commands;
using SonicCube.Shared.Ipc;
using SonicCube.Tray.Device;
using SonicCube.Tray.Settings;

namespace SonicCube.Tray.Ipc;

/// <summary>
/// Tray 的 Named Pipe Server(见需求第九节)。
/// 仅当前用户可连接;单 Panel 客户端;支持快照 + 订阅 + 电平合并最新值广播。
/// </summary>
public sealed class IpcServer
{
    public const string PipeName = "SonicCubePanel.IPC";

    private readonly DeviceCoordinator _coordinator;
    private readonly WorkModeManager _modeManager;
    private readonly SettingsStore _settings;

    private readonly object _sync = new();
    // Capture, version and enqueue together. Connection transitions and device updates
    // otherwise race GetState replies, making a newer version carry older state.
    private readonly object _stateSync = new();
    private PipeConnection? _client;
    private CancellationTokenSource? _listenCts;
    private CancellationTokenSource? _clientLifetime;
    private long _stateVersion;
    private volatile bool _clientSubscribed;

    // 电平合并最新值
    private float[]? _pendingMeters;
    private int _meterBroadcastBusy;

    public IpcServer(DeviceCoordinator coordinator, WorkModeManager modeManager, SettingsStore settings)
    {
        _coordinator = coordinator;
        _modeManager = modeManager;
        _settings = settings;

        _coordinator.DeviceMessage += OnDeviceMessage;
        _coordinator.Meters += OnMeters;
        _coordinator.StateChanged += OnConnStateChanged;
        _settings.Changed += _ => BroadcastSettings();
    }

    public void Start()
    {
        _listenCts = new CancellationTokenSource();
        _ = Task.Run(() => ListenLoopAsync(_listenCts.Token));
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var conn = new PipeConnection(pipe);
                pipe = null; // 所有权移交
                OnClientConnected(conn);
                // 等待该客户端断开后再接受下一个(单 Panel)
                var tcs = new TaskCompletionSource();
                conn.Disconnected += () => tcs.TrySetResult();
                conn.StartReadLoop();
                await tcs.Task.ConfigureAwait(false);
                OnClientDisconnected(conn);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logging.Logger.Error("IPC", $"监听循环异常: {ex.Message}");
                try { await Task.Delay(500, ct); } catch { }
            }
            finally
            {
                pipe?.Dispose();
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        // 仅当前用户可访问。
        var ps = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        ps.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: IpcFraming.MaxMessageBytes + 2,
            outBufferSize: IpcFraming.MaxMessageBytes + 2,
            ps);
    }

    private void OnClientConnected(PipeConnection conn)
    {
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_listenCts!.Token);
        var cancellationToken = lifetime.Token;
        lock (_sync) { _client = conn; _clientLifetime = lifetime; }
        conn.Disconnected += lifetime.Cancel;
        conn.MessageReceived += msg => OnClientMessage(conn, msg, cancellationToken);
        Logging.Logger.Info("IPC", "Panel 已连接");
        _modeManager.NotifyPanelOpened();
        BroadcastDeviceStatus();
    }

    private void OnClientDisconnected(PipeConnection conn)
    {
        CancellationTokenSource? lifetime = null;
        lock (_sync)
        {
            if (ReferenceEquals(_client, conn)) { _client = null; lifetime = _clientLifetime; _clientLifetime = null; }
        }
        if (lifetime is not null) { lifetime.Cancel(); conn.Disconnected -= lifetime.Cancel; lifetime.Dispose(); }
        _clientSubscribed = false;
        conn.Dispose();
        Logging.Logger.Info("IPC", "Panel 已断开");
        _modeManager.NotifyPanelClosed();
    }

    // ── 消息处理 ────────────────────────────────────────────

    private void OnClientMessage(PipeConnection conn, IpcMessage msg, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return;
        switch (msg.Type)
        {
            case IpcMessageType.GetState:
                _ = SendStateSnapshotAsync(conn, msg.RequestId);
                break;
            case IpcMessageType.GetSettings:
                _ = SendSettingsAsync(conn, msg.RequestId);
                break;
            case IpcMessageType.Subscribe:
                _clientSubscribed = true;
                _ = ReplyAsync(conn, msg.RequestId, true);
                // 订阅后立即推送一次当前快照:避免 Panel 在设备握手完成前连接拿到空快照后无广播。
                _ = SendStateSnapshotAsync(conn, null);
                break;
            case IpcMessageType.Unsubscribe:
                _clientSubscribed = false;
                _ = ReplyAsync(conn, msg.RequestId, true);
                break;
            case IpcMessageType.ToggleSwitch:
                HandleToggle(conn, msg, cancellationToken);
                break;
            case IpcMessageType.SetParameter:
                HandleSetParameter(conn, msg, cancellationToken);
                break;
            case IpcMessageType.ResetModule:
                HandleResetModule(conn, msg, cancellationToken);
                break;
            case IpcMessageType.SetWorkMode:
                HandleSetWorkMode(conn, msg);
                break;
            case IpcMessageType.UpdateSettings:
                HandleUpdateSettings(conn, msg);
                break;
            default:
                _ = SendErrorAsync(conn, msg.RequestId, "unknown_type", $"未知消息类型 {msg.Type}");
                break;
        }
    }

    private static TimeSpan RequestBudget(IpcMessage msg) => TimeSpan.FromMilliseconds(
        msg.DeadlineUnixMs is long deadline
            ? Math.Clamp((double)deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 0, 15000)
            : 15000);

    private async Task FinishCommandAsync(PipeConnection conn, IpcMessage msg, Task<CommandResult> command)
    {
        var result = await command.ConfigureAwait(false);
        if (result.ErrorCode != "superseded") _modeManager.NotifyUserActivity();
        await ReplyAsync(conn, msg.RequestId, result.Success, result.ErrorCode, result.ErrorDetail).ConfigureAwait(false);
    }

    private void HandleToggle(PipeConnection conn, IpcMessage msg, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<QuickSwitch>(msg.Switch, ignoreCase: true, out var sw))
        {
            Logging.Logger.Warn("IPC", $"无效开关: '{msg.Switch ?? "(null)"}'");
            _ = SendErrorAsync(conn, msg.RequestId, "bad_switch", "无效开关");
            return;
        }
        _ = FinishCommandAsync(conn, msg, _coordinator.ToggleSwitchAsync(sw, ActionSource.ManualPanel, RequestBudget(msg), cancellationToken));
    }

    private void HandleSetParameter(PipeConnection conn, IpcMessage msg, CancellationToken cancellationToken)
    {
        // Enqueue on the pipe reader before awaiting: Task.Run here can reorder an algorithm
        // selection/reset and the parameter writes that follow it.
        Task<CommandResult> command;
        var budget = RequestBudget(msg);
        if (msg.Address.HasValue && msg.IntValue.HasValue)
            command = _coordinator.SetParameterAsync(msg.Address.Value, msg.IntValue.Value, ActionSource.ManualPanel, budget, cancellationToken);
        else if (msg.Address.HasValue && msg.FloatValue.HasValue)
            command = _coordinator.SetParameterFloatAsync(msg.Address.Value, msg.FloatValue.Value, ActionSource.ManualPanel, budget, cancellationToken);
        else if (msg.Module.HasValue && msg.ParamIndex.HasValue && msg.FloatValue.HasValue)
            command = _coordinator.SetFxParameterAsync(msg.Module.Value, msg.ParamIndex.Value, msg.FloatValue.Value, ActionSource.ManualPanel, budget, cancellationToken);
        else
        {
            _ = SendErrorAsync(conn, msg.RequestId, "bad_param", "参数不完整");
            return;
        }
        _ = FinishCommandAsync(conn, msg, command);
    }

    private void HandleSetWorkMode(PipeConnection conn, IpcMessage msg)
    {
        var mode = string.Equals(msg.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase)
            ? WorkMode.FastConnection : WorkMode.LowResource;
        if (RequestBudget(msg) <= TimeSpan.Zero) { _ = ReplyAsync(conn, msg.RequestId, false, "timeout"); return; }
        bool saved = _modeManager.TrySetMode(mode, out var error);
        _ = ReplyAsync(conn, msg.RequestId, saved, saved ? null : "settings_save", error);
    }

    private void HandleResetModule(PipeConnection conn, IpcMessage msg, CancellationToken cancellationToken)
    {
        if (!msg.Module.HasValue)
        {
            _ = SendErrorAsync(conn, msg.RequestId, "bad_param", "缺少 module");
            return;
        }
        _ = FinishCommandAsync(conn, msg, _coordinator.ResetModuleAsync(msg.Module.Value, ActionSource.ManualPanel, RequestBudget(msg), cancellationToken));
    }

    private void HandleUpdateSettings(PipeConnection conn, IpcMessage msg)
    {
        var dto = msg.Settings;
        if (dto == null)
        {
            _ = SendErrorAsync(conn, msg.RequestId, "bad_settings", "缺少 settings");
            return;
        }
        if (RequestBudget(msg) <= TimeSpan.Zero) { _ = ReplyAsync(conn, msg.RequestId, false, "timeout"); return; }
        var previousMode = _settings.Current.Mode;
        bool saved = _settings.TryApplyUpdate(s =>
        {
            if (dto.Language != null) s.Language = dto.Language;
            if (dto.StartWithWindows.HasValue) s.StartWithWindows = dto.StartWithWindows.Value;
            if (dto.IdleDisconnectSeconds.HasValue) s.IdleDisconnectSeconds = dto.IdleDisconnectSeconds.Value;
            if (dto.Enable48V.HasValue) s.Startup.Enable48V = dto.Enable48V.Value;
            if (dto.EnableEFX.HasValue) s.Startup.EnableEFX = dto.EnableEFX.Value;
            if (dto.ReapplyOnReconnect.HasValue) s.Startup.ReapplyOnReconnect = dto.ReapplyOnReconnect.Value;
            if (dto.PhantomPowerConsent.HasValue) s.Startup.PhantomPowerConsent = dto.PhantomPowerConsent.Value;
            if (dto.AlwaysOnTop.HasValue) s.Panel.AlwaysOnTop = dto.AlwaysOnTop.Value;
            if (dto.WorkMode != null)
                s.Mode = string.Equals(dto.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase)
                    ? WorkMode.FastConnection : WorkMode.LowResource;
        }, out var error);
        if (saved)
        {
            if (dto.StartWithWindows.HasValue) Startup.StartupManager.Apply(_settings.Current.StartWithWindows);
            _modeManager.ApplyPersistedMode(previousMode);
        }
        _ = ReplyAsync(conn, msg.RequestId, saved, saved ? null : "settings_save", error);
    }

    // ── 快照 / 广播 ─────────────────────────────────────────

    private Task SendStateSnapshotAsync(PipeConnection conn, string? requestId)
    {
        lock (_stateSync)
        {
            var dto = _coordinator.GetStateSnapshot();
            return conn.SendAsync(new IpcMessage
            {
                Type = IpcMessageType.CommandResult,
                RequestId = requestId,
                Success = true,
                Version = ++_stateVersion,
                State = dto,
            });
        }
    }

    private async Task SendSettingsAsync(PipeConnection conn, string? requestId)
    {
        var s = _settings.Current;
        var dto = new SettingsDto
        {
            Language = s.Language,
            StartWithWindows = s.StartWithWindows,
            WorkMode = s.WorkModeValue,
            IdleDisconnectSeconds = s.IdleDisconnectSeconds,
            Enable48V = s.Startup.Enable48V,
            EnableEFX = s.Startup.EnableEFX,
            ReapplyOnReconnect = s.Startup.ReapplyOnReconnect,
            PhantomPowerConsent = s.Startup.PhantomPowerConsent,
            AlwaysOnTop = s.Panel.AlwaysOnTop,
        };
        await conn.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.CommandResult,
            RequestId = requestId,
            Success = true,
            Settings = dto,
        });
    }

    private void OnDeviceMessage(uint cmdAddr, byte[] data)
    {
        // 电平包已由 Meters 通道单独推送,跳过快照广播,避免 25fps 下重复构建全量 DTO。
        if (CommandAddress.TypeOf(cmdAddr) == CommandAddress.PickOut) return;
        if (!_clientSubscribed) return;
        lock (_stateSync)
        {
            var dto = _coordinator.GetStateSnapshot();
            Broadcast(new IpcMessage
            {
                Type = IpcMessageType.StateChanged,
                Version = ++_stateVersion,
                State = dto,
                DeviceStatus = dto.Connected ? "connected" : "disconnected",
            });
        }
    }

    private void OnMeters(float[] m)
    {
        if (!_clientSubscribed) return;
        // 合并最新值:仅保留最新电平,防止积压。
        Interlocked.Exchange(ref _pendingMeters, m);
        if (Interlocked.Exchange(ref _meterBroadcastBusy, 1) == 0)
            _ = FlushMetersAsync();
    }

    private async Task FlushMetersAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(40)); // ~25 FPS
        try
        {
            while (true)
            {
                var m = Interlocked.Exchange(ref _pendingMeters, null);
                if (m == null) break;
                Broadcast(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = m });
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _meterBroadcastBusy, 0);
            // A last update can arrive after the empty read but before busy is cleared.
            if (Volatile.Read(ref _pendingMeters) != null && Interlocked.Exchange(ref _meterBroadcastBusy, 1) == 0)
                _ = FlushMetersAsync();
        }
    }

    private void OnConnStateChanged(ConnectionState st) => BroadcastDeviceStatus();

    private void BroadcastDeviceStatus()
    {
        lock (_stateSync)
        {
            var dto = _coordinator.GetStateSnapshot();
            Broadcast(new IpcMessage
            {
                Type = IpcMessageType.DeviceStatusChanged,
                Version = ++_stateVersion,
                State = dto,
                DeviceStatus = dto.Connected ? "connected" : "disconnected",
            });
        }
    }

    private void BroadcastSettings()
    {
        if (!_clientSubscribed) return;
        var s = _settings.Current;
        Broadcast(new IpcMessage
        {
            Type = IpcMessageType.StateChanged,
            Settings = new SettingsDto
            {
                Language = s.Language,
                StartWithWindows = s.StartWithWindows,
                WorkMode = s.WorkModeValue,
                IdleDisconnectSeconds = s.IdleDisconnectSeconds,
                Enable48V = s.Startup.Enable48V,
                EnableEFX = s.Startup.EnableEFX,
                ReapplyOnReconnect = s.Startup.ReapplyOnReconnect,
                PhantomPowerConsent = s.Startup.PhantomPowerConsent,
                AlwaysOnTop = s.Panel.AlwaysOnTop,
            },
        });
    }

    private void Broadcast(IpcMessage msg)
    {
        PipeConnection? c;
        lock (_sync) c = _client;
        if (c == null || !c.IsConnected) return;
        // 可靠广播与请求/响应保持 FIFO。电平在写泵内再次合并最新值,
        // 管道阻塞时不会把每个 40ms 的旧电平堆积起来。
        c.Post(msg);
    }

    private static async Task ReplyAsync(PipeConnection conn, string? requestId, bool success, string? errCode = null, string? errDetail = null)
    {
        try
        {
            await conn.SendAsync(new IpcMessage
            {
                Type = IpcMessageType.CommandResult,
                RequestId = requestId,
                Success = success,
                ErrorCode = errCode,
                ErrorDetail = errDetail,
            });
        }
        catch { }
    }

    private static async Task SendErrorAsync(PipeConnection conn, string? requestId, string code, string detail)
    {
        try
        {
            await conn.SendAsync(new IpcMessage
            {
                Type = IpcMessageType.Error,
                RequestId = requestId,
                ErrorCode = code,
                ErrorDetail = detail,
            });
        }
        catch { }
    }

    public void Stop()
    {
        _listenCts?.Cancel();
        // Let OnClientDisconnected own lifetime cleanup after the read loop unwinds.
        PipeConnection? client;
        lock (_sync) client = _client;
        client?.Dispose();
    }
}

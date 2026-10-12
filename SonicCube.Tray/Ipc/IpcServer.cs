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
    private PipeConnection? _client;
    private CancellationTokenSource? _listenCts;
    private long _stateVersion;
    private bool _clientSubscribed;

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
        lock (_sync) _client = conn;
        conn.MessageReceived += msg => OnClientMessage(conn, msg);
        Logging.Logger.Info("IPC", "Panel 已连接");
        _modeManager.NotifyPanelOpened();
        BroadcastDeviceStatus();
    }

    private void OnClientDisconnected(PipeConnection conn)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_client, conn)) _client = null;
        }
        _clientSubscribed = false;
        conn.Dispose();
        Logging.Logger.Info("IPC", "Panel 已断开");
        _modeManager.NotifyPanelClosed();
    }

    // ── 消息处理 ────────────────────────────────────────────

    private void OnClientMessage(PipeConnection conn, IpcMessage msg)
    {
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
                HandleToggle(conn, msg);
                break;
            case IpcMessageType.SetParameter:
                HandleSetParameter(conn, msg);
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

    private void HandleToggle(PipeConnection conn, IpcMessage msg)
    {
        if (!Enum.TryParse<QuickSwitch>(msg.Switch, ignoreCase: true, out var sw))
        {
            Logging.Logger.Warn("IPC", $"无效开关: '{msg.Switch ?? "(null)"}'");
            _ = SendErrorAsync(conn, msg.RequestId, "bad_switch", "无效开关");
            return;
        }
        _ = Task.Run(async () =>
        {
            var r = await _coordinator.ToggleSwitchAsync(sw, ActionSource.ManualPanel);
            _modeManager.NotifyUserActivity();
            await ReplyAsync(conn, msg.RequestId, r.Success, r.ErrorCode, r.ErrorDetail);
        });
    }

    private void HandleSetParameter(PipeConnection conn, IpcMessage msg)
    {
        _ = Task.Run(async () =>
        {
            CommandResult r;
            if (msg.Address.HasValue && msg.IntValue.HasValue)
                r = await _coordinator.SetParameterAsync(msg.Address.Value, msg.IntValue.Value, ActionSource.ManualPanel);
            else if (msg.Address.HasValue && msg.FloatValue.HasValue)
                r = await _coordinator.SetParameterFloatAsync(msg.Address.Value, msg.FloatValue.Value, ActionSource.ManualPanel);
            else if (msg.Module.HasValue && msg.ParamIndex.HasValue && msg.FloatValue.HasValue)
                r = await _coordinator.SetFxParameterAsync(msg.Module.Value, msg.ParamIndex.Value, msg.FloatValue.Value, ActionSource.ManualPanel);
            else
            {
                await SendErrorAsync(conn, msg.RequestId, "bad_param", "参数不完整");
                return;
            }
            _modeManager.NotifyUserActivity();
            await ReplyAsync(conn, msg.RequestId, r.Success, r.ErrorCode, r.ErrorDetail);
        });
    }

    private void HandleSetWorkMode(PipeConnection conn, IpcMessage msg)
    {
        var mode = string.Equals(msg.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase)
            ? WorkMode.FastConnection : WorkMode.LowResource;
        _modeManager.SetMode(mode);
        _ = ReplyAsync(conn, msg.RequestId, true);
    }

    private void HandleUpdateSettings(PipeConnection conn, IpcMessage msg)
    {
        var dto = msg.Settings;
        if (dto == null)
        {
            _ = SendErrorAsync(conn, msg.RequestId, "bad_settings", "缺少 settings");
            return;
        }
        var s = _settings.Current;
        if (dto.Language != null) s.Language = dto.Language;
        if (dto.StartWithWindows.HasValue)
        {
            s.StartWithWindows = dto.StartWithWindows.Value;
            Startup.StartupManager.Apply(s.StartWithWindows);
        }
        if (dto.IdleDisconnectSeconds.HasValue) s.IdleDisconnectSeconds = dto.IdleDisconnectSeconds.Value;
        if (dto.Enable48V.HasValue) s.Startup.Enable48V = dto.Enable48V.Value;
        if (dto.EnableEFX.HasValue) s.Startup.EnableEFX = dto.EnableEFX.Value;
        if (dto.ReapplyOnReconnect.HasValue) s.Startup.ReapplyOnReconnect = dto.ReapplyOnReconnect.Value;
        if (dto.PhantomPowerConsent.HasValue) s.Startup.PhantomPowerConsent = dto.PhantomPowerConsent.Value;
        if (dto.AlwaysOnTop.HasValue) s.Panel.AlwaysOnTop = dto.AlwaysOnTop.Value;
        _settings.Save();

        // 工作模式单独走 SetMode 以触发运行时切换。
        if (dto.WorkMode != null)
        {
            var mode = string.Equals(dto.WorkMode, "fastConnection", StringComparison.OrdinalIgnoreCase)
                ? WorkMode.FastConnection : WorkMode.LowResource;
            _modeManager.SetMode(mode);
        }
        _ = ReplyAsync(conn, msg.RequestId, true);
    }

    // ── 快照 / 广播 ─────────────────────────────────────────

    private async Task SendStateSnapshotAsync(PipeConnection conn, string? requestId)
    {
        var dto = BuildStateDto();
        await conn.SendAsync(new IpcMessage
        {
            Type = IpcMessageType.CommandResult,
            RequestId = requestId,
            Success = true,
            Version = Interlocked.Read(ref _stateVersion),
            State = dto,
        });
    }

    private DeviceStateDto BuildStateDto()
    {
        var g = _coordinator.Session?.Global;
        var p = _coordinator.Session?.Preset;
        var dto = new DeviceStateDto { Connected = _coordinator.IsConnected };
        if (g != null)
        {
            dto.Phantom48V = g.Phantom48V; dto.Efx = g.Efx; dto.ReverbOn = g.ReverbOn;
            dto.Monitor = g.Monitor; dto.OnAir = g.OnAir; dto.MasterMute = g.MasterMute;
        }
        if (p != null)
        {
            Array.Copy(p.InverseSw, dto.InverseSw, 2);
            Array.Copy(p.HpfSw, dto.HpfSw, 2);
            Array.Copy(p.LowCut, dto.LowCut, 2);
            Array.Copy(p.LoopbackSw, dto.LoopbackSw, 2);
            dto.DspRecSw = p.DspRecSw; dto.FxModuleEn = p.FxModuleEn;
            Array.Copy(p.FxAlg, dto.FxAlg, 6);
            Array.Copy(p.FxParaVal, dto.FxParaVal, Math.Min(96, p.FxParaVal.Length));
            Array.Copy(p.MuteSw, dto.MuteSw, 4);
            Array.Copy(p.SoloSw, dto.SoloSw, 2);
            Array.Copy(p.VolPot, dto.VolPot, 4);
            Array.Copy(p.Phase, dto.Phase, 2);
            Array.Copy(p.Send, dto.Send, 2);
            dto.MonoSw = p.MonoSw;
        }
        return dto;
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
        Interlocked.Increment(ref _stateVersion);
        if (!_clientSubscribed) return;
        // 对订阅端广播整包/单项状态变化。为控制频率,仅转发并附带版本号。
        var dto = BuildStateDto();
        Broadcast(new IpcMessage
        {
            Type = IpcMessageType.StateChanged,
            Version = Interlocked.Read(ref _stateVersion),
            State = dto,
            DeviceStatus = _coordinator.IsConnected ? "connected" : "disconnected",
        });
    }

    private void OnMeters(float[] m)
    {
        if (!_clientSubscribed) return;
        // 合并最新值:仅保留最新电平,防止积压。
        _pendingMeters = m;
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
                var m = _pendingMeters;
                _pendingMeters = null;
                if (m == null) break;
                Broadcast(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = m });
                await timer.WaitForNextTickAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _meterBroadcastBusy, 0);
        }
    }

    private void OnConnStateChanged(ConnectionState st) => BroadcastDeviceStatus();

    private void BroadcastDeviceStatus()
    {
        Broadcast(new IpcMessage
        {
            Type = IpcMessageType.DeviceStatusChanged,
            DeviceStatus = _coordinator.IsConnected ? "connected" : "disconnected",
        });
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
        _ = Task.Run(async () =>
        {
            try { await c.SendAsync(msg); } catch { /* 客户端断开 */ }
        });
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
        lock (_sync) { _client?.Dispose(); _client = null; }
    }
}

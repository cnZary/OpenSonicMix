using System.IO.Pipes;
using System.IO;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Ipc;

/// <summary>
/// Panel 的 IPC 客户端(见需求 9.3/9.4)。
/// 连接 Tray 命名管道,订阅状态,快照同步,电平接收,命令发送。
/// 所有设备操作经 IPC 转交 Tray;Panel 不直接打开 MIDI。
/// </summary>
public sealed class TrayClient : IDisposable
{
    private PipeConnection? _conn;
    private int _requestSeq;
    private readonly Dictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly object _pendingSync = new();

    public bool IsConnected => _conn?.IsConnected == true;

    /// <summary>设备状态快照(最新)。</summary>
    public DeviceStateDto? State { get; private set; }
    public long StateVersion { get; private set; }

    public event Action<DeviceStateDto>? StateUpdated;      // 全量/单项状态更新
    public event Action<float[]>? MetersUpdated;             // 电平
    public event Action<bool>? ConnectionChanged;            // 设备连接状态
    public event Action<SettingsDto>? SettingsUpdated;
    public event Action? TrayDisconnected;                   // 与 Tray 的 IPC 断开

    /// <summary>连接 Tray(若未运行则可选启动它)。</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var pipe = new NamedPipeClientStream(".", TrayPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(1500, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            return false;
        }
        catch (Exception)
        {
            pipe.Dispose();
            return false;
        }

        _conn = new PipeConnection(pipe);
        _conn.MessageReceived += OnMessage;
        _conn.Disconnected += OnDisconnected;
        _conn.StartReadLoop();
        return true;
    }

    public const string TrayPipeName = "SonicCubePanel.IPC";

    /// <summary>确保连接;失败时尝试启动 Tray 进程后重连。</summary>
    public async Task<bool> EnsureConnectedAsync(string? trayExePath, CancellationToken ct = default)
    {
        if (IsConnected) return true;
        if (await ConnectAsync(ct)) return true;

        // 启动 Tray
        if (trayExePath != null && File.Exists(trayExePath))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(trayExePath) { UseShellExecute = true });
            }
            catch { }
            // 等待其就绪
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(250, ct);
                if (await ConnectAsync(ct)) return true;
            }
        }
        return false;
    }

    /// <summary>启动同步:订阅 + 获取全量快照(带版本号防丢事件)。</summary>
    public async Task<DeviceStateDto?> SubscribeAndSyncAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(new IpcMessage { Type = IpcMessageType.Subscribe }, ct);
        var resp = await SendRequestAsync(new IpcMessage { Type = IpcMessageType.GetState }, ct);
        if (resp?.State != null)
        {
            State = resp.State;
            StateVersion = resp.Version ?? 0;
            StateUpdated?.Invoke(State);
        }
        return State;
    }

    public async Task<SettingsDto?> GetSettingsAsync(CancellationToken ct = default)
    {
        var resp = await SendRequestAsync(new IpcMessage { Type = IpcMessageType.GetSettings }, ct);
        return resp?.Settings;
    }

    public Task<IpcMessage?> UpdateSettingsAsync(SettingsDto dto, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.UpdateSettings, Settings = dto }, ct);

    public Task<IpcMessage?> SetWorkModeAsync(string mode, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.SetWorkMode, WorkMode = mode }, ct);

    public Task<IpcMessage?> ToggleAsync(string sw, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.ToggleSwitch, Switch = sw }, ct);

    public Task<IpcMessage?> SetIntAsync(uint address, int value, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.SetParameter, Address = address, IntValue = value }, ct);

    public Task<IpcMessage?> SetFloatAsync(uint address, float value, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.SetParameter, Address = address, FloatValue = value }, ct);

    public Task<IpcMessage?> SetFxParamAsync(int module, int paramIndex, float value, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.SetParameter, Module = module, ParamIndex = paramIndex, FloatValue = value }, ct);

    /// <summary>尽力发送退订(关闭时调用,不等待)。</summary>
    public async Task SendUnsubscribeQuiet()
    {
        try
        {
            if (IsConnected && _conn != null)
                await _conn.SendAsync(new IpcMessage { Type = IpcMessageType.Unsubscribe });
        }
        catch { }
    }

    private async Task<IpcMessage?> SendRequestAsync(IpcMessage msg, CancellationToken ct)
    {
        if (!IsConnected) return null;
        msg.RequestId = $"r{Interlocked.Increment(ref _requestSeq)}";
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingSync) _pending[msg.RequestId] = tcs;
        try
        {
            await _conn!.SendAsync(msg, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using (timeout.Token.Register(() => tcs.TrySetCanceled()).ConfigureAwait(false))
                return await tcs.Task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            lock (_pendingSync) _pending.Remove(msg.RequestId);
        }
    }

    private void OnMessage(IpcMessage msg)
    {
        // 响应配对
        if (msg.RequestId != null)
        {
            lock (_pendingSync)
            {
                if (_pending.TryGetValue(msg.RequestId, out var tcs))
                {
                    tcs.TrySetResult(msg);
                    return;
                }
            }
        }

        switch (msg.Type)
        {
            case IpcMessageType.StateChanged:
                if (msg.State != null)
                {
                    State = msg.State;
                    if (msg.Version.HasValue) StateVersion = msg.Version.Value;
                    StateUpdated?.Invoke(msg.State);
                }
                if (msg.Settings != null) SettingsUpdated?.Invoke(msg.Settings);
                break;
            case IpcMessageType.MeterUpdate:
                if (msg.Meters != null) MetersUpdated?.Invoke(msg.Meters);
                break;
            case IpcMessageType.DeviceStatusChanged:
                ConnectionChanged?.Invoke(msg.DeviceStatus == "connected");
                break;
        }
    }

    private void OnDisconnected()
    {
        TrayDisconnected?.Invoke();
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }
}

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
    private bool _disposed;
    private int _disconnectRaised;
    private readonly string _pipeName;
    public TrayClient() : this(TrayPipeName) { }
    internal TrayClient(string pipeName) => _pipeName = pipeName;
    private int _requestSeq;
    private readonly Dictionary<string, TaskCompletionSource<IpcMessage>> _pending = new();
    private readonly object _pendingSync = new();
    // 并发调用共享同一次连接/拉起(Tray 进程最多被启动一次)。
    private readonly SemaphoreSlim _connectGate = new(1, 1);

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
    public async Task<bool> ConnectAsync(int timeoutMs = 1500, CancellationToken ct = default)
    {
        if (_disposed) return false;
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
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

        if (_disposed) { pipe.Dispose(); return false; }
        var conn = new PipeConnection(pipe);
        _conn = conn;
        Interlocked.Exchange(ref _disconnectRaised, 0);
        State = null;
        StateVersion = 0;
        conn.MessageReceived += msg => { if (ReferenceEquals(_conn, conn)) OnMessage(msg); };
        conn.Disconnected += () => { if (ReferenceEquals(_conn, conn)) OnDisconnected(); };
        conn.StartReadLoop();
        return true;
    }

    public const string TrayPipeName = "SonicCubePanel.IPC";

    /// <summary>
    /// 确保连接;失败时立即启动 Tray 进程后重连。
    /// 并发调用经信号量串行(共享同一次连接/拉起);首轮探测用短超时,拉起后密集轮询。
    /// </summary>
    public async Task<bool> EnsureConnectedAsync(string? trayExePath, CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (IsConnected) return true;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return true; // 另一调用者已连上
            // 首轮短超时:Tray 已在运行时管道即连即通;不在时避免白等。
            if (await ConnectAsync(400, ct)) return true;

            // 启动 Tray
            if (trayExePath != null && File.Exists(trayExePath))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(trayExePath) { UseShellExecute = true });
                }
                catch { }
                // 等待其就绪(Tray 为 NativeAOT 秒开,密集轮询尽快接管)
                for (int i = 0; i < 30; i++)
                {
                    if (await ConnectAsync(150, ct)) return true;
                    await Task.Delay(100, ct);
                }
            }
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>启动同步:订阅 + 获取全量快照(带版本号防丢事件)。</summary>
    public async Task<DeviceStateDto?> SubscribeAndSyncAsync(CancellationToken ct = default)
    {
        await SendRequestAsync(new IpcMessage { Type = IpcMessageType.Subscribe }, ct);
        await SendRequestAsync(new IpcMessage { Type = IpcMessageType.GetState }, ct);
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

    public Task<IpcMessage?> ResetModuleAsync(int module, CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.ResetModule, Module = module }, ct);

    /// <summary>直接拉取一次设备状态快照(不依赖订阅广播)。</summary>
    public async Task<DeviceStateDto?> GetStateDirectAsync(CancellationToken ct = default)
    {
        var resp = await SendRequestAsync(new IpcMessage { Type = IpcMessageType.GetState }, ct);
        return resp?.State is not null ? State : null;
    }

    /// <summary>单独订阅(重连后重新建立电平/状态广播)。</summary>
    public Task<IpcMessage?> SubscribeAsync(CancellationToken ct = default) =>
        SendRequestAsync(new IpcMessage { Type = IpcMessageType.Subscribe }, ct);

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
        var conn = _conn;
        if (conn?.IsConnected != true) return Failure("disconnected");
        msg.RequestId = $"r{Interlocked.Increment(ref _requestSeq)}";
        msg.DeadlineUnixMs = DateTimeOffset.UtcNow.AddSeconds(20).ToUnixTimeMilliseconds();
        var tcs = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingSync) _pending[msg.RequestId] = tcs;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await conn.SendAsync(msg, timeout.Token).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure("timeout");
        }
        catch { return Failure("disconnected"); }
        finally
        {
            lock (_pendingSync) _pending.Remove(msg.RequestId);
        }
    }

    private void OnMessage(IpcMessage msg)
    {
        // Apply snapshots on the ordered read loop, before waking request continuations.
        bool currentVersion = !msg.Version.HasValue || msg.Version.Value >= StateVersion;
        if (msg.State != null && currentVersion)
        {
            State = msg.State;
            StateVersion = msg.Version ?? StateVersion;
            StateUpdated?.Invoke(msg.State);
        }
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
                if (msg.Settings != null) SettingsUpdated?.Invoke(msg.Settings);
                break;
            case IpcMessageType.MeterUpdate:
                if (msg.Meters != null) MetersUpdated?.Invoke(msg.Meters);
                break;
            case IpcMessageType.DeviceStatusChanged:
                if (currentVersion) ConnectionChanged?.Invoke(msg.DeviceStatus == "connected");
                break;
        }
    }

    private void OnDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectRaised, 1) != 0) return;
        TaskCompletionSource<IpcMessage>[] pending;
        lock (_pendingSync) { pending = _pending.Values.ToArray(); _pending.Clear(); }
        foreach (var waiter in pending) waiter.TrySetResult(Failure("disconnected"));
        TrayDisconnected?.Invoke();
    }

    private static IpcMessage Failure(string code) => new()
        { Type = IpcMessageType.CommandResult, Success = false, ErrorCode = code };

    public void Dispose()
    {
        _disposed = true;
        _conn?.Dispose();
        OnDisconnected();
    }
}

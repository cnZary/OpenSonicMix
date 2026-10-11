using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Models;
using SonicCube.Shared.Midi;

namespace SonicCube.Tray.Device;

/// <summary>设备连接状态。</summary>
public enum ConnectionState
{
    /// <summary>未连接,端口关闭。</summary>
    Disconnected,
    /// <summary>正在枚举/打开/握手。</summary>
    Connecting,
    /// <summary>已连接并完成握手、状态同步。</summary>
    Connected,
    /// <summary>(快速连接模式)设备掉线,等待重新发现。</summary>
    WaitingForDevice,
}

/// <summary>
/// 统一异步命令协调器(见需求第七节)。
/// 单工作线程串行处理所有 MIDI 操作:连接/断开/Toggle/写参数/自动动作。
/// 保证:不并发建连、不并发握手、命令与关端口不冲突、异常不崩溃、发送线程唯一。
/// </summary>
public sealed class DeviceCoordinator
{
    private readonly object _sync = new();
    private readonly Queue<Func<CancellationToken, Task>> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Thread _worker;
    private readonly CancellationTokenSource _shutdown = new();
    private volatile bool _running = true;

    private DeviceSession? _session;
    private ConnectionState _state = ConnectionState.Disconnected;
    private long _connectEpoch; // 每次成功建连递增,用于区分「可信离线→在线」转换与过期操作

    // 同一开关的进行中操作去重
    private readonly HashSet<QuickSwitch> _inFlightSwitches = new();

    public event Action<ConnectionState>? StateChanged;
    public event Action? Connected;                 // 建连+握手+状态同步完成
    public event Action? Disconnected;
    public event Action<float[]>? Meters;
    /// <summary>设备消息更新(供 IPC 广播 state_changed)。cmdAddr + data。</summary>
    public event Action<uint, byte[]>? DeviceMessage;

    public ConnectionState State { get { lock (_sync) return _state; } }
    public bool IsConnected => State == ConnectionState.Connected;
    public long ConnectEpoch { get { lock (_sync) return _connectEpoch; } }

    /// <summary>快速开关的最后已知状态(低占用模式释放后仍可用,见需求 4.2 菜单勾选)。</summary>
    public sealed class SwitchStateSnapshot
    {
        public int Phantom48V { get; set; }
        public int Efx { get; set; }
        public int Monitor { get; set; }
        public int OnAir { get; set; }
        public int MasterMute { get; set; }
        public int MonoSw { get; set; }
        /// <summary>是否已有有效数据(从未成功连接过则为 false)。</summary>
        public bool HasData { get; set; }
    }

    // 会话存活期间的最新状态 + 断开前最后一次快照
    private readonly SwitchStateSnapshot _lastSnapshot = new();

    /// <summary>获取六个快速开关的当前已知状态(已连接=实时;低占用空闲=断开前最后状态)。</summary>
    public SwitchStateSnapshot GetSwitchStates()
    {
        lock (_sync)
        {
            var s = _session;
            if (s != null && IsConnected)
            {
                return new SwitchStateSnapshot
                {
                    Phantom48V = s.Global.Phantom48V,
                    Efx = s.Global.Efx,
                    Monitor = s.Global.Monitor,
                    OnAir = s.Global.OnAir,
                    MasterMute = s.Global.MasterMute,
                    MonoSw = s.Preset.MonoSw,
                    HasData = true,
                };
            }
            return new SwitchStateSnapshot
            {
                Phantom48V = _lastSnapshot.Phantom48V,
                Efx = _lastSnapshot.Efx,
                Monitor = _lastSnapshot.Monitor,
                OnAir = _lastSnapshot.OnAir,
                MasterMute = _lastSnapshot.MasterMute,
                MonoSw = _lastSnapshot.MonoSw,
                HasData = _lastSnapshot.HasData,
            };
        }
    }

    /// <summary>断开前把会话状态快照到 _lastSnapshot,供低占用模式菜单回显。</summary>
    private void CaptureLastSnapshot()
    {
        var s = _session;
        if (s == null) return;
        lock (_sync)
        {
            _lastSnapshot.Phantom48V = s.Global.Phantom48V;
            _lastSnapshot.Efx = s.Global.Efx;
            _lastSnapshot.Monitor = s.Global.Monitor;
            _lastSnapshot.OnAir = s.Global.OnAir;
            _lastSnapshot.MasterMute = s.Global.MasterMute;
            _lastSnapshot.MonoSw = s.Preset.MonoSw;
            _lastSnapshot.HasData = true;
        }
    }

    /// <summary>Panel 是否打开(打开时禁止低占用模式空闲断开)。</summary>
    public volatile bool PanelOpen;

    /// <summary>是否有待执行或进行中的命令(阻止空闲断开)。</summary>
    public bool HasPendingWork
    {
        get { lock (_sync) return _queue.Count > 0 || _busy; }
    }
    private bool _busy;

    public DeviceCoordinator()
    {
        _worker = new Thread(() => WorkLoopAsync().GetAwaiter().GetResult()) { IsBackground = true, Name = "SonicCube.DeviceCoordinator" };
        _worker.Start();
    }

    private void SetState(ConnectionState s)
    {
        lock (_sync) _state = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>把操作入队,立即返回(不阻塞调用方/消息循环)。</summary>
    public void Enqueue(Func<CancellationToken, Task> op)
    {
        lock (_sync) _queue.Enqueue(op);
        _signal.Release();
    }
    /// <summary>入队并等待完成(带超时),返回结果。供 IPC/需要回执的路径。</summary>
    public Task<CommandResult> EnqueueAndWaitAsync(Func<CancellationToken, Task<CommandResult>> op, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(async ct =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
                if (timeout.HasValue) linked.CancelAfter(timeout.Value);
                var r = await op(linked.Token).ConfigureAwait(false);
                tcs.TrySetResult(r);
            }
            catch (OperationCanceledException)
            {
                tcs.TrySetResult(CommandResult.Fail("timeout", "命令执行超时或被取消"));
            }
            catch (Exception ex)
            {
                Logging.Logger.Error("Cmd", $"命令异常: {ex.Message}");
                tcs.TrySetResult(CommandResult.Fail("error", ex.Message));
            }
        });
        return tcs.Task;
    }

    private async Task WorkLoopAsync()
    {
        try
        {
            while (_running)
            {
                Func<CancellationToken, Task>? op = null;
                lock (_sync)
                {
                    if (_queue.Count > 0) { op = _queue.Dequeue(); _busy = true; }
                }
                if (op == null)
                {
                    try { await _signal.WaitAsync(200).ConfigureAwait(false); }
                    catch (Exception ex) { Logging.Logger.Error("Cmd", $"WaitAsync 异常: {ex.Message}"); }
                    continue;
                }
                try
                {
                    await op(_shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logging.Logger.Error("Cmd", $"工作循环异常: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    lock (_sync) _busy = false;
                    try { CommandCompleted?.Invoke(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Error("Cmd", $"工作线程终止: {ex.GetType().Name}: {ex.Message}");
        }
        Logging.Logger.Error("Cmd", "工作线程已退出(_running=false)");
    }

    /// <summary>每条命令完成后触发(用于重置空闲计时)。</summary>
    public event Action? CommandCompleted;

    // ── 连接管理 ────────────────────────────────────────────

    /// <summary>请求建立连接并完成握手(若已连接则直接成功)。幂等,不并发。</summary>
    public Task<CommandResult> EnsureConnectedAsync(ActionSource source, TimeSpan? timeout = null)
    {
        return EnqueueAndWaitAsync(async ct =>
        {
            if (IsConnected && _session != null) return CommandResult.Ok();
            return await ConnectCoreAsync(source, ct).ConfigureAwait(false);
        }, timeout ?? TimeSpan.FromSeconds(15));
    }

    private async Task<CommandResult> ConnectCoreAsync(ActionSource source, CancellationToken ct)
    {
        if (IsConnected && _session != null) return CommandResult.Ok();
        SetState(ConnectionState.Connecting);
        try
        {
            // 释放旧会话
            DisposeSession();
            Logging.Logger.Info("Device", "枚举并打开 MIDI 端口...");

            var session = await Task.Run(() => DeviceSession.OpenFirst(), ct).ConfigureAwait(false);
            session.MetersReceived += OnMeters;
            session.MessageReceived += OnDeviceMessage;
            session.RawFrame += OnRawFrame;
            _session = session;
            Logging.Logger.Info("Device", "端口已打开,开始握手...");

            await session.HandshakeAsync(ct).ConfigureAwait(false);

            lock (_sync) _connectEpoch++;
            SetState(ConnectionState.Connected);
            Logging.Logger.Info("Device", $"已连接 (source={source}, epoch={ConnectEpoch})");
            Connected?.Invoke();
            return CommandResult.Ok();
        }
        catch (MidiException ex)
        {
            Logging.Logger.Warn("Device", $"连接失败: {ex.Message}");
            DisposeSession();
            SetState(ConnectionState.Disconnected);
            return CommandResult.Fail("open_failed", ex.Message);
        }
        catch (OperationCanceledException)
        {
            DisposeSession();
            SetState(ConnectionState.Disconnected);
            return CommandResult.Fail("timeout", "连接/握手超时");
        }
        catch (Exception ex)
        {
            Logging.Logger.Error("Device", $"连接异常: {ex.Message}");
            DisposeSession();
            SetState(ConnectionState.Disconnected);
            return CommandResult.Fail("error", ex.Message);
        }
    }

    /// <summary>
    /// 连接存活检测:当前会话的输入/输出端口名是否仍能在系统中枚举到。
    /// WinMM 不会主动通知 SysEx 端口消失(物理拔出),供快速连接模式低频扫描调用。
    /// 会话为空、未打开或枚举异常时返回 false;轻量、不抛异常、线程安全。
    /// </summary>
    public bool IsConnectionAlive()
    {
        try
        {
            DeviceSession? s;
            lock (_sync) s = _session;
            var dev = s?.Device;
            if (dev == null || !dev.IsOpen) return false;
            string? inName = dev.InName, outName = dev.OutName;
            if (string.IsNullOrEmpty(inName) || string.IsNullOrEmpty(outName)) return false;

            bool inFound = false, outFound = false;
            foreach (var (_, name) in MidiDevice.EnumerateInputs())
                if (string.Equals(name, inName, StringComparison.Ordinal)) { inFound = true; break; }
            foreach (var (_, name) in MidiDevice.EnumerateOutputs())
                if (string.Equals(name, outName, StringComparison.Ordinal)) { outFound = true; break; }
            return inFound && outFound;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>主动断开并释放端口(空闲释放/模式切换/退出)。</summary>
    public Task<CommandResult> DisconnectAsync(string reason)
    {
        return EnqueueAndWaitAsync(ct =>
        {
            Logging.Logger.Info("Device", $"断开连接: {reason}");
            DisposeSession();
            SetState(ConnectionState.Disconnected);
            Disconnected?.Invoke();
            return Task.FromResult(CommandResult.Ok());
        });
    }

    private void DisposeSession()
    {
        var s = _session;
        _session = null;
        if (s != null)
        {
            CaptureLastSnapshot();
            try { s.MetersReceived -= OnMeters; s.MessageReceived -= OnDeviceMessage; s.RawFrame -= OnRawFrame; }
            catch { }
            try { s.Dispose(); } catch { }
        }
    }

    /// <summary>当前会话(仅工作线程使用)。</summary>
    public DeviceSession? Session => _session;

    // ── 事件转发 ────────────────────────────────────────────

    private void OnMeters(float[] m)
    {
        // Panel 未打开时不做电平业务处理(见 8.5)。回调只完成缓冲处理。
        if (PanelOpen) Meters?.Invoke(m);
        Logging.Logger.Meter(m);
    }

    private void OnDeviceMessage(uint cmdAddr, byte[] data)
    {
        DeviceMessage?.Invoke(cmdAddr, data);
    }

    private void OnRawFrame(bool tx, byte[] frame)
    {
        // 原始帧日志(含 cmdAddr 提取供排查)。
        uint addr = 0;
        if (frame.Length >= 21)
        {
            // 解首个 nibble 对(cmdAddr 高字节先发)。简化:仅记录十六进制。
        }
        Logging.Logger.MidiFrame(tx, addr, frame);
    }

    // ── 快速开关 Toggle ─────────────────────────────────────

    /// <summary>
    /// 执行快速开关 Toggle(见 7.2):读当前有效状态 → 计算目标 → 发送 → 等回包 → 验证 → 更新缓存。
    /// 同一开关进行中时抑制重复输入;不同开关排队。MONO 走 PHASE 联动组合。
    /// </summary>
    public Task<CommandResult> ToggleSwitchAsync(QuickSwitch sw, ActionSource source)
    {
        lock (_sync)
        {
            if (_inFlightSwitches.Contains(sw))
            {
                Logging.Logger.Debug("Cmd", $"开关 {sw} 正在执行,抑制重复输入");
                // 返回一个已完成的「被合并」结果,避免意外多次反转。
                return Task.FromResult(CommandResult.Fail("busy", "该开关正在执行"));
            }
            _inFlightSwitches.Add(sw);
        }

        return EnqueueAndWaitAsync(async ct =>
        {
            try
            {
                Logging.Logger.Info("Cmd", $"Toggle {sw} 开始 (source={source}, connected={IsConnected})");
                // 确保连接(复用当前连接与缓存,不重复全量拉取)。
                var c = await EnsureConnectedInternalAsync(source, ct).ConfigureAwait(false);
                if (!c.Success) { Logging.Logger.Warn("Cmd", $"Toggle {sw} 连接失败: {c.ErrorCode}"); return c; }
                var r = await DoToggleAsync(sw, ct).ConfigureAwait(false);
                Logging.Logger.Info("Cmd", $"Toggle {sw} (source={source}) → {(r.Success ? "成功" : r.ErrorCode)}");
                return r;
            }
            finally
            {
                lock (_sync) _inFlightSwitches.Remove(sw);
            }
        }, TimeSpan.FromSeconds(20));
    }

    private Task<CommandResult> EnsureConnectedInternalAsync(ActionSource source, CancellationToken ct) =>
        IsConnected && _session != null
            ? Task.FromResult(CommandResult.Ok())
            : ConnectCoreAsync(source, ct);

    private async Task<CommandResult> DoToggleAsync(QuickSwitch sw, CancellationToken ct)
    {
        var s = _session!;
        switch (sw)
        {
            case QuickSwitch.Phantom48V:
            {
                int cur = s.Global.Phantom48V;
                int target = cur == 1 ? 0 : 1;
                await s.SetIntAsync(CommandAddress.Phantom48V, target, ct).ConfigureAwait(false);
                return s.Global.Phantom48V == target ? CommandResult.Ok() : CommandResult.Fail("verify", "48V 回包值与目标不符");
            }
            case QuickSwitch.Efx:
            {
                int cur = s.Global.Efx, target = cur == 1 ? 0 : 1;
                await s.SetIntAsync(CommandAddress.Efx, target, ct).ConfigureAwait(false);
                return s.Global.Efx == target ? CommandResult.Ok() : CommandResult.Fail("verify", "EFX 回包值与目标不符");
            }
            case QuickSwitch.Monitor:
            {
                int cur = s.Global.Monitor, target = cur == 1 ? 0 : 1;
                await s.SetIntAsync(CommandAddress.Monitor, target, ct).ConfigureAwait(false);
                return s.Global.Monitor == target ? CommandResult.Ok() : CommandResult.Fail("verify", "MONITOR 回包值与目标不符");
            }
            case QuickSwitch.OnAir:
            {
                int cur = s.Global.OnAir, target = cur == 1 ? 0 : 1;
                await s.SetIntAsync(CommandAddress.OnAir, target, ct).ConfigureAwait(false);
                return s.Global.OnAir == target ? CommandResult.Ok() : CommandResult.Fail("verify", "ONAIR 回包值与目标不符");
            }
            case QuickSwitch.MasterMute:
            {
                int cur = s.Global.MasterMute, target = cur == 1 ? 0 : 1;
                await s.SetIntAsync(CommandAddress.MasterMute, target, ct).ConfigureAwait(false);
                return s.Global.MasterMute == target ? CommandResult.Ok() : CommandResult.Fail("verify", "总静音回包值与目标不符");
            }
            case QuickSwitch.Mono:
                return await ToggleMonoAsync(ct).ConfigureAwait(false);
            default:
                return CommandResult.Fail("unknown", "未知开关");
        }
    }

    /// <summary>
    /// MONO 联动(见 7.2):开 = MONO=1 + 两通道 PHASE=0;关 = MONO=0 + PHASE -50/+50。
    /// 保持顺序;部分失败时记录并返回失败。
    /// </summary>
    private async Task<CommandResult> ToggleMonoAsync(CancellationToken ct)
    {
        var s = _session!;
        int cur = s.Preset.MonoSw;
        int target = cur == 1 ? 0 : 1;
        // MONO 是 int 开关,等设备确认回包。
        var r = await WriteIntVerifiedAsync(CommandAddress.Mono, target, ct).ConfigureAwait(false);
        if (!r.Success) return r;

        // PHASE 联动(float,设备不回确认包且偶发丢包):逐通道发送→读回,不一致重发。
        float p0 = target == 1 ? 0f : -50f;
        float p1 = target == 1 ? 0f : 50f;
        var r0 = await WriteFloatVerifiedAsync(CommandAddress.WithLocation(CommandAddress.Phase, 0), p0, ct).ConfigureAwait(false);
        var r1 = await WriteFloatVerifiedAsync(CommandAddress.WithLocation(CommandAddress.Phase, 1), p1, ct).ConfigureAwait(false);
        if (!r0.Success || !r1.Success)
            return CommandResult.Fail("verify", $"PHASE 联动失败(ch0={r0.ErrorCode}, ch1={r1.ErrorCode})");
        return CommandResult.Ok();
    }

    // ── 通用参数写入(供 IPC set_parameter)─────────────────

    /// <summary>
    /// 写 int 并验证。设备对「无变化的写入」可能不回包,电平洪峰期回包也可能迟到;
    /// 超时后读回当前值兜底验证,避免假超时(见需求 7.3:发送完成不代表成功,但已正确的值不应报错)。
    /// </summary>
    private async Task<CommandResult> WriteIntVerifiedAsync(uint fullAddress, int value, CancellationToken ct)
    {
        var s = _session!;
        try
        {
            await s.SetIntAsync(fullAddress, value, ct).ConfigureAwait(false);
            return CommandResult.Ok();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 回包超时:读回验证设备当前实际值。
            int cur = ReadCurrentInt(fullAddress);
            if (cur == value) return CommandResult.Ok();
            return CommandResult.Fail("timeout", "设备未确认写入");
        }
    }

    private async Task<CommandResult> WriteFloatVerifiedAsync(uint fullAddress, float value, CancellationToken ct)
    {
        var s = _session!;
        // float 参数(PHASE/SEND/VOLPOT)设备不回确认包且偶发丢包:发送→读回,不一致重发,最多 3 次。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await s.SendFloatNoConfirmAsync(fullAddress, value, ct).ConfigureAwait(false);
            await Task.Delay(70, ct).ConfigureAwait(false); // 留设备应用时间
            try
            {
                await s.RequestAsync(CommandAddress.Preset, ct).ConfigureAwait(false); // 读回刷新缓存
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt == 2) return CommandResult.Fail("timeout", "读回验证超时");
                continue;
            }
            float cur = ReadCurrentFloat(fullAddress);
            if (Math.Abs(cur - value) < 0.05f) return CommandResult.Ok();
        }
        float final = ReadCurrentFloat(fullAddress);
        return CommandResult.Fail("verify", $"读回值 {final:0.00} 与目标 {value:0.00} 不符");
    }

    private int ReadCurrentInt(uint fullAddress)
    {
        var s = _session!;
        uint type = CommandAddress.TypeOf(fullAddress);
        int loc = CommandAddress.LocationOf(fullAddress);
        return type switch
        {
            CommandAddress.Efx => s.Global.Efx,
            CommandAddress.Phantom48V => s.Global.Phantom48V,
            CommandAddress.Monitor => s.Global.Monitor,
            CommandAddress.OnAir => s.Global.OnAir,
            CommandAddress.MasterMute => s.Global.MasterMute,
            CommandAddress.Mono => s.Preset.MonoSw,
            CommandAddress.Inverse => loc < 2 ? s.Preset.InverseSw[loc] : -1,
            CommandAddress.HpfSwitch => loc < 2 ? s.Preset.HpfSw[loc] : -1,
            CommandAddress.LowCut => loc < 2 ? s.Preset.LowCut[loc] : -1,
            CommandAddress.MuteSwitch => loc < 4 ? s.Preset.MuteSw[loc] : -1,
            CommandAddress.SoloSwitch => loc < 2 ? s.Preset.SoloSw[loc] : -1,
            CommandAddress.Loopback => loc < 2 ? s.Preset.LoopbackSw[loc] : -1,
            CommandAddress.DspRecSwitch => s.Preset.DspRecSw,
            CommandAddress.FxAlgorithm => loc < 6 ? s.Preset.FxAlg[loc] : -1,
            CommandAddress.FxModuleEnable => s.Preset.FxModuleEn,
            _ => int.MinValue,
        };
    }

    private float ReadCurrentFloat(uint fullAddress)
    {
        var s = _session!;
        uint type = CommandAddress.TypeOf(fullAddress);
        int loc = CommandAddress.LocationOf(fullAddress);
        return type switch
        {
            CommandAddress.VolumePot => loc < 4 ? s.Preset.VolPot[loc] : float.NaN,
            CommandAddress.Phase => loc < 2 ? s.Preset.Phase[loc] : float.NaN,
            CommandAddress.Send => loc < 2 ? s.Preset.Send[loc] : float.NaN,
            _ => float.NaN,
        };
    }

    public Task<CommandResult> SetParameterAsync(uint fullAddress, int value, ActionSource source) =>
        EnqueueAndWaitAsync(async ct =>
        {
            var c = await EnsureConnectedInternalAsync(source, ct).ConfigureAwait(false);
            if (!c.Success) return c;
            return await WriteIntVerifiedAsync(fullAddress, value, ct).ConfigureAwait(false);
        }, TimeSpan.FromSeconds(15));

    public Task<CommandResult> SetParameterFloatAsync(uint fullAddress, float value, ActionSource source) =>
        EnqueueAndWaitAsync(async ct =>
        {
            var c = await EnsureConnectedInternalAsync(source, ct).ConfigureAwait(false);
            if (!c.Success) return c;
            return await WriteFloatVerifiedAsync(fullAddress, value, ct).ConfigureAwait(false);
        }, TimeSpan.FromSeconds(15));

    public Task<CommandResult> SetFxParameterAsync(int module, int paramIndex, float value, ActionSource source) =>
        EnqueueAndWaitAsync(async ct =>
        {
            var c = await EnsureConnectedInternalAsync(source, ct).ConfigureAwait(false);
            if (!c.Success) return c;
            var s = _session!;
            // FX 参数(float)设备不回确认包且偶发丢包:发送→读回,不一致重发,最多 3 次。
            for (int attempt = 0; attempt < 3; attempt++)
            {
                await s.SendFxParameterNoConfirmAsync(module, paramIndex, value, ct).ConfigureAwait(false);
                await Task.Delay(70, ct).ConfigureAwait(false);
                try { await s.RequestAsync(CommandAddress.Preset, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt == 2) return CommandResult.Fail("timeout", "FX 参数读回超时");
                    continue;
                }
                float cur = s.Preset.GetFxPara(module, paramIndex);
                if (Math.Abs(cur - value) < 0.05f) return CommandResult.Ok();
            }
            float final = s.Preset.GetFxPara(module, paramIndex);
            return CommandResult.Fail("verify", $"FX 参数读回值 {final:0.00} 与目标 {value:0.00} 不符");
        }, TimeSpan.FromSeconds(20));

    public void Shutdown()
    {
        _running = false;
        _shutdown.Cancel();
        try { _signal.Release(); } catch { }
        DisposeSession();
        try { _worker.Join(2000); } catch { }
    }
}

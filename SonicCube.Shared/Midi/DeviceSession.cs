using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Frames;
using SonicCube.Protocol.Models;

namespace SonicCube.Shared.Midi;

/// <summary>
/// 高层 Sonic Cube II 会话:打开 MIDI、分片发送、重组接收、握手、状态缓存、读写与回包验证。
/// 发送线程安全(锁);接收经 <see cref="SysExFrame.Parser"/> 重组后分发。
/// </summary>
public sealed class DeviceSession : IDisposable
{
    /// <summary>设备回包等待超时。</summary>
    public TimeSpan ResponseTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>连续分片发送间隔(约 1ms,见 PROTOCOL.md 8.1)。</summary>
    public TimeSpan ChunkInterval { get; set; } = TimeSpan.FromMilliseconds(1);

    private readonly MidiDevice _device;
    private readonly SysExFrame.Parser _parser = new();
    private readonly object _recvSync = new();

    // 按 cmdAddr 等待回包(同一时刻一个等待者)。
    private readonly Dictionary<uint, TaskCompletionSource<byte[]>> _pending = new();

    public PresetState Preset { get; } = new();
    public GlobalState Global { get; } = new();
    public bool IsConnected { get; private set; }
    public MidiDevice Device => _device;

    /// <summary>收到设备消息(cmdAddr, data 段)时触发(握手与内部等待之前先经过 pending 分发)。</summary>
    public event Action<uint, byte[]>? MessageReceived;
    /// <summary>电平包(0x000B0011)到达。</summary>
    public event Action<float[]>? MetersReceived;
    /// <summary>原始帧记录(方向 + 字节),仅日志用。</summary>
    public event Action<bool /*tx*/, byte[]>? RawFrame;

    public DeviceSession(MidiDevice device)
    {
        _device = device;
        _device.DataReceived += OnDeviceData;
    }

    public static DeviceSession OpenFirst()
    {
        var dev = MidiDevice.OpenFirstMatching();
        return new DeviceSession(dev);
    }

    private void OnDeviceData(byte[] bytes)
    {
        RawFrame?.Invoke(false, bytes);
        var completed = new List<(uint, byte[])>();
        lock (_recvSync)
            _parser.Feed(bytes, completed);

        foreach (var (cmdAddr, data) in completed)
        {
            // 更新状态缓存
            uint type = CommandAddress.TypeOf(cmdAddr);
            int location = CommandAddress.LocationOf(cmdAddr);
            if (cmdAddr == CommandAddress.Preset)
                CopyPreset(PresetState.Parse(data));
            else if (cmdAddr == CommandAddress.Global)
                CopyGlobal(GlobalState.Parse(data));
            else if (type == CommandAddress.PickOut)
                MetersReceived?.Invoke(GlobalState.ParseMeters(data));
            else if (cmdAddr != CommandAddress.SoftwareState && cmdAddr != CommandAddress.FirmwareState)
            {
                // 单项响应/确认:按页更新
                if ((cmdAddr & 0xFF) == 0x10)
                    Preset.ApplyIndividual(type, location, data);
                else if ((cmdAddr & 0xFF) == 0x11 && data.Length >= 4)
                    Global.ApplyIndividual(type, Payload.ReadInt32(data));
            }

            // 唤醒等待者
            if (_pending.TryGetValue(cmdAddr, out var tcs) && _pending.Remove(cmdAddr))
                tcs.TrySetResult(data);

            MessageReceived?.Invoke(cmdAddr, data);
        }
    }

    private void CopyPreset(PresetState src)
    {
        Array.Copy(src.InverseSw, Preset.InverseSw, 2);
        Array.Copy(src.HpfSw, Preset.HpfSw, 2);
        Array.Copy(src.LowCut, Preset.LowCut, 2);
        Array.Copy(src.LoopbackSw, Preset.LoopbackSw, 2);
        Preset.DspRecSw = src.DspRecSw;
        Preset.FxModuleEn = src.FxModuleEn;
        Array.Copy(src.FxSlot, Preset.FxSlot, src.FxSlot.Length);
        Array.Copy(src.FxAlg, Preset.FxAlg, src.FxAlg.Length);
        Array.Copy(src.FxParaVal, Preset.FxParaVal, src.FxParaVal.Length);
        Array.Copy(src.MuteSw, Preset.MuteSw, 4);
        Array.Copy(src.SoloSw, Preset.SoloSw, 2);
        Array.Copy(src.VolPot, Preset.VolPot, 4);
        Array.Copy(src.Phase, Preset.Phase, 2);
        Array.Copy(src.Send, Preset.Send, 2);
        Preset.MonoSw = src.MonoSw;
    }

    private void CopyGlobal(GlobalState src)
    {
        Global.HiEn = src.HiEn;
        Global.Phantom48V = src.Phantom48V;
        Global.Efx = src.Efx;
        Global.ReverbOn = src.ReverbOn;
        Global.Monitor = src.Monitor;
        Global.OnAir = src.OnAir;
        Global.MasterMute = src.MasterMute;
        Global.AutoOutput = src.AutoOutput;
    }

    /// <summary>分片发送一条消息(flag + 数据段)。</summary>
    public async Task SendAsync(uint cmdAddr, FrameFlag flag, byte[] data, CancellationToken ct = default)
    {
        var frames = SysExFrame.Encode(cmdAddr, flag, data);
        for (int i = 0; i < frames.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            _device.SendSysEx(frames[i]);
            RawFrame?.Invoke(true, frames[i]);
            if (i < frames.Length - 1 && ChunkInterval > TimeSpan.Zero)
                await Task.Delay(ChunkInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>请求重试次数(设备可能在电平包洪峰或忙时丢包,与官方 300ms 轮询一致)。</summary>
    public int RequestRetries { get; set; } = 4;
    /// <summary>设置重试次数(设置不重复执行副作用,仅当未收到回包时重发)。</summary>
    public int SetRetries { get; set; } = 3;

    /// <summary>发送请求并等待同 cmdAddr 回包,带有限重试。</summary>
    public async Task<byte[]> RequestAsync(uint cmdAddr, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await RequestOnceAsync(cmdAddr, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < RequestRetries - 1)
            {
                // 单次响应超时,重试。
            }
        }
    }

    private async Task<byte[]> RequestOnceAsync(uint cmdAddr, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[cmdAddr] = tcs;
        try
        {
            await SendAsync(cmdAddr, FrameFlag.Request, [], ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeout);
            await using (timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token)).ConfigureAwait(false))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.Remove(cmdAddr);
        }
    }

    /// <summary>发送设置并等待同 cmdAddr 确认回包(见 7.3:发送完成不代表成功),带有限重试。</summary>
    public async Task<byte[]> SetAsync(uint cmdAddr, byte[] data, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await SetOnceAsync(cmdAddr, data, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < SetRetries - 1)
            {
                // 单次响应超时,重发。设置是幂等的(写绝对值,非 Toggle),重发安全。
            }
        }
    }

    private async Task<byte[]> SetOnceAsync(uint cmdAddr, byte[] data, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[cmdAddr] = tcs;
        try
        {
            await SendAsync(cmdAddr, FrameFlag.Set, data, ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ResponseTimeout);
            await using (timeout.Token.Register(() => tcs.TrySetCanceled(timeout.Token)).ConfigureAwait(false))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _pending.Remove(cmdAddr);
        }
    }

    public Task<byte[]> SetIntAsync(uint cmdAddr, int value, CancellationToken ct = default) =>
        SetAsync(cmdAddr, Payload.Int32(value), ct);

    public Task<byte[]> SetFloatAsync(uint cmdAddr, float value, CancellationToken ct = default) =>
        SetAsync(cmdAddr, Payload.Float32(value), ct);

    public Task<byte[]> SetFxParameterAsync(int module, int paramIndex, float value, CancellationToken ct = default) =>
        SetAsync(CommandAddress.FxParameter, Payload.FxParameter(module, paramIndex, value), ct);

    /// <summary>
    /// 发送 float 设置但不等设备确认回包(实测:PHASE/SEND/VOLPOT 等 float 参数设备不回确认包)。
    /// 发送后由调用方通过 <see cref="RequestAsync"/> 读回验证。
    /// </summary>
    public Task SendFloatNoConfirmAsync(uint cmdAddr, float value, CancellationToken ct = default) =>
        SendAsync(cmdAddr, FrameFlag.Set, Payload.Float32(value), ct);

    /// <summary>发送 int 设置但不等设备确认回包。</summary>
    public Task SendIntNoConfirmAsync(uint cmdAddr, int value, CancellationToken ct = default) =>
        SendAsync(cmdAddr, FrameFlag.Set, Payload.Int32(value), ct);

    /// <summary>发送效果参数但不等设备确认回包。</summary>
    public Task SendFxParameterNoConfirmAsync(int module, int paramIndex, float value, CancellationToken ct = default) =>
        SendAsync(CommandAddress.FxParameter, FrameFlag.Set, Payload.FxParameter(module, paramIndex, value), ct);

    /// <summary>标准握手:请求 SOFTWARE_STATE → 整包预设 → 全部全局参数,初始化缓存。</summary>
    public async Task HandshakeAsync(CancellationToken ct = default)
    {
        await RequestAsync(CommandAddress.SoftwareState, ct).ConfigureAwait(false);
        await RequestAsync(CommandAddress.Preset, ct).ConfigureAwait(false);
        await RequestAsync(CommandAddress.Global, ct).ConfigureAwait(false);
        IsConnected = true;
    }

    public void Dispose()
    {
        IsConnected = false;
        _device.DataReceived -= OnDeviceData;
        _device.Dispose();
    }
}

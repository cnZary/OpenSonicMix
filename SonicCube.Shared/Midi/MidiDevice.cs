using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace SonicCube.Shared.Midi;

/// <summary>
/// WinMM 输入/输出配对。原生回调只投递事件；顺序工作泵负责归还输入缓冲和分发数据。
/// 输出缓冲由设备持有到 MOM_DONE 或 reset 归还，取消等待不释放驱动仍在使用的内存。
/// </summary>
public sealed class MidiDevice : IDisposable
{
    public const string DeviceNameSubstring = "Sonic Cube II";
    private const int InBufferCount = 4;
    private const int InBufferSize = 4096;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WinMM.MIDIHDR>();
    // A broken driver may refuse to release a buffer even after reset. Keep both memory and
    // callback delegates rooted until cleanup succeeds, rather than freeing driver-owned memory.
    private static readonly ConcurrentDictionary<MidiDevice, byte> ClosingDevices = new();
    private readonly IWinMmApi _api;
    private readonly object _sync = new();
    private readonly Channel<NativeEvent> _events = Channel.CreateUnbounded<NativeEvent>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });
    private readonly Dictionary<IntPtr, NativeBuffer> _inputs = new();
    private readonly Dictionary<IntPtr, NativeBuffer> _outputs = new();
    private readonly WinMM.MidiInProc _inProc;
    private readonly WinMM.MidiOutProc _outProc;
    private IntPtr _hIn, _hOut;
    private int _generation;
    private bool _disposed, _closing;
    private int? _closeRetryGeneration;
    private volatile bool _isOpen;

    private readonly record struct NativeEvent(int Generation, int Message, IntPtr Header,
        int Retry = 0, NativeBuffer? ExpectedOutput = null);

    private sealed class NativeBuffer : IDisposable
    {
        private GCHandle _pin;
        public IntPtr Header { get; private set; }
        public bool Prepared { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NativeBuffer(byte[] data)
        {
            _pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                Header = Marshal.AllocHGlobal((int)HeaderSize);
                Marshal.StructureToPtr(new WinMM.MIDIHDR
                {
                    lpData = _pin.AddrOfPinnedObject(), dwBufferLength = (uint)data.Length,
                    dwReserved = new IntPtr[8],
                }, Header, false);
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            if (Header != IntPtr.Zero) { Marshal.FreeHGlobal(Header); Header = IntPtr.Zero; }
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    public event Action<byte[]>? DataReceived;
    public event Action? Closed;
    public event Action<Exception>? ReceiveError;
    public string? InName { get; private set; }
    public string? OutName { get; private set; }
    public bool IsOpen => _isOpen;

    public MidiDevice() : this(new WinMmApi()) { }
    internal MidiDevice(IWinMmApi api)
    {
        _api = api;
        _inProc = InCallback;
        _outProc = OutCallback;
        _ = Task.Run(ProcessEventsAsync);
    }
    /// <summary>枚举所有名字包含 "Sonic Cube II" 的输入端口索引。</summary>
    public static List<(uint Index, string Name)> EnumerateInputs()
    {
        var list = new List<(uint, string)>();
        uint n = WinMM.midiInGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            if (WinMM.midiInGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WinMM.MIDIINCAPSW>()) == WinMM.MMSYSERR_NOERROR
                && caps.Name.Contains(DeviceNameSubstring, StringComparison.OrdinalIgnoreCase))
                list.Add((i, caps.Name));
        }
        return list;
    }

    /// <summary>枚举所有名字包含 "Sonic Cube II" 的输出端口索引。</summary>
    public static List<(uint Index, string Name)> EnumerateOutputs()
    {
        var list = new List<(uint, string)>();
        uint n = WinMM.midiOutGetNumDevs();
        for (uint i = 0; i < n; i++)
        {
            if (WinMM.midiOutGetDevCaps((UIntPtr)i, out var caps, (uint)Marshal.SizeOf<WinMM.MIDIOUTCAPSW>()) == WinMM.MMSYSERR_NOERROR
                && caps.Name.Contains(DeviceNameSubstring, StringComparison.OrdinalIgnoreCase))
                list.Add((i, caps.Name));
        }
        return list;
    }

    /// <summary>找到第一对匹配的输入/输出端口并打开。失败抛出 <see cref="MidiException"/>。</summary>
    public static MidiDevice OpenFirstMatching()
    {
        var ins = EnumerateInputs();
        var outs = EnumerateOutputs();
        if (ins.Count == 0 || outs.Count == 0)
            throw new MidiException($"未找到名字包含 \"{DeviceNameSubstring}\" 的 MIDI 输入/输出端口");
        var dev = new MidiDevice();
        try
        {
            dev.Open(ins[0].Index, outs[0].Index, ins[0].Name, outs[0].Name);
            return dev;
        }
        catch { dev.Dispose(); throw; }
    }

    public void Open(uint inIndex, uint outIndex, string inName, string outName)
    {
        try
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_isOpen) return;
                if (_closing) throw new MidiException("上一次 MIDI 连接仍在释放缓冲");
                var instance = (IntPtr)(++_generation);
                Check(_api.OpenInput(out _hIn, inIndex, _inProc, instance), "midiInOpen");
                Check(_api.OpenOutput(out _hOut, outIndex, _outProc, instance), "midiOutOpen");
                for (int i = 0; i < InBufferCount; i++)
                {
                    var buffer = new NativeBuffer(new byte[InBufferSize]);
                    _inputs.Add(buffer.Header, buffer);
                    Check(_api.PrepareInput(_hIn, buffer.Header, HeaderSize), "midiInPrepareHeader");
                    buffer.Prepared = true;
                    Check(_api.AddInputBuffer(_hIn, buffer.Header, HeaderSize), "midiInAddBuffer");
                }
                InName = inName;
                OutName = outName;
                _isOpen = true;
                Check(_api.StartInput(_hIn), "midiInStart");
            }
        }
        catch { Close(); throw; }
    }

    /// <summary>兼容同步调用方；UI 和异步会话应使用 SendSysExAsync。</summary>
    public void SendSysEx(byte[] frame) => SendSysExAsync(frame).GetAwaiter().GetResult();

    public async Task SendSysExAsync(byte[] frame, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        NativeBuffer buffer;
        lock (_sync)
        {
            if (!_isOpen || _hOut == IntPtr.Zero) throw new MidiException("MIDI 输出未打开");
            // Own a copy: callers can reuse their array once their wait is cancelled.
            buffer = new NativeBuffer((byte[])frame.Clone());
            _outputs.Add(buffer.Header, buffer);
            try
            {
                Check(_api.PrepareOutput(_hOut, buffer.Header, HeaderSize), "midiOutPrepareHeader");
                buffer.Prepared = true;
                Check(_api.SendOutput(_hOut, buffer.Header, HeaderSize), "midiOutLongMsg");
            }
            catch (Exception ex)
            {
                if (!TryReleaseOutput(buffer))
                {
                    // A rejected send can still leave a prepared header owned by the driver.
                    // Report the original failure now and retry cleanup without freeing it early.
                    buffer.Completion.TrySetException(ex);
                    _ = buffer.Completion.Task.Exception;
                    _ = RetryEventAsync(new(_generation, WinMM.MM_MOM_DONE, buffer.Header, ExpectedOutput: buffer));
                }
                throw;
            }
        }
        await buffer.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    // Never take _sync, dereference a header, call WinMM, or run managed subscribers here.
    // WinMM may synchronously invoke callbacks while another thread owns _sync in reset/send.
    private void InCallback(IntPtr h, int message, IntPtr instance, IntPtr header, IntPtr unused)
    {
        if (message is WinMM.MM_MIM_LONGDATA or WinMM.MM_MIM_LONGERROR)
            _events.Writer.TryWrite(new((int)instance, message, header));
    }

    private void OutCallback(IntPtr h, int message, IntPtr instance, IntPtr header, IntPtr unused)
    {
        if (message == WinMM.MM_MOM_DONE)
            _events.Writer.TryWrite(new((int)instance, message, header));
    }

    private async Task ProcessEventsAsync()
    {
        await foreach (var ev in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                byte[]? data = null;
                lock (_sync)
                {
                    // Queued callbacks may outlive Close, or even a subsequent Open.
                    if (ev.Generation != _generation) continue;
                    if (_closing) { TryFinishClose(); continue; }
                    if (ev.Message == WinMM.MM_MOM_DONE)
                    {
                        if (!_outputs.TryGetValue(ev.Header, out var output)
                            || (ev.ExpectedOutput != null && !ReferenceEquals(output, ev.ExpectedOutput))) continue;
                        if (TryReleaseOutput(output))
                        {
                            output.Completion.TrySetResult();
                        }
                        else
                        {
                            if (ev.Retry >= 100)
                            {
                                var error = new MidiException("MIDI 驱动未释放已完成的发送缓冲");
                                output.Completion.TrySetException(error);
                                throw error;
                            }
                            // A late callback can release this header before the timer fires, and
                            // the allocator may reuse its address. Retry only this buffer instance.
                            _ = RetryEventAsync(ev with { ExpectedOutput = output });
                        }
                    }
                    else if (_isOpen && _inputs.TryGetValue(ev.Header, out var input))
                    {
                        var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(input.Header);
                        if (ev.Message == WinMM.MM_MIM_LONGDATA && hdr.dwBytesRecorded > 0)
                        {
                            int length = (int)Math.Min(hdr.dwBytesRecorded, hdr.dwBufferLength);
                            data = new byte[length];
                            Marshal.Copy(hdr.lpData, data, 0, length);
                        }
                        hdr.dwBytesRecorded = 0;
                        Marshal.StructureToPtr(hdr, input.Header, false);
                        Check(_api.AddInputBuffer(_hIn, input.Header, HeaderSize), "midiInAddBuffer");
                    }
                }
                // Input is copied and returned before parsing/broadcasting. Subscribers run in order.
                if (data != null) DataReceived?.Invoke(data);
            }
            catch (Exception ex)
            {
                try { ReceiveError?.Invoke(ex); } catch { }
            }
        }
    }

    private async Task RetryEventAsync(NativeEvent ev)
    {
        await Task.Delay(10).ConfigureAwait(false);
        _events.Writer.TryWrite(ev with { Retry = ev.Retry + 1 });
    }

    private bool TryReleaseOutput(NativeBuffer buffer)
    {
        if (buffer.Prepared && _api.UnprepareOutput(_hOut, buffer.Header, HeaderSize) != WinMM.MMSYSERR_NOERROR)
            return false;
        // Remove before clearing Header so failed-send callers cannot leave a stale entry.
        _outputs.Remove(buffer.Header);
        buffer.Dispose();
        return true;
    }

    public void Close()
    {
        bool notify;
        lock (_sync)
        {
            notify = !_closing && (_isOpen || _hIn != IntPtr.Zero || _hOut != IntPtr.Zero);
            _isOpen = false;
            _closing = true;
            if (_hIn != IntPtr.Zero) { _api.StopInput(_hIn); _api.ResetInput(_hIn); }
            if (_hOut != IntPtr.Zero) _api.ResetOutput(_hOut);
            foreach (var output in _outputs.Values) output.Completion.TrySetCanceled();
            TryFinishClose();
            if (_closing && _closeRetryGeneration != _generation)
            {
                _closeRetryGeneration = _generation;
                _ = RetryCloseAsync(_generation);
            }
        }
        if (notify) Closed?.Invoke();
    }

    private async Task RetryCloseAsync(int generation)
    {
        // Bounded cleanup retries cover drivers whose reset completion trails reset's return.
        // If a driver still owns memory afterward, retain it and accept later completion events.
        for (int i = 0; i < 20; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
            lock (_sync)
            {
                if (generation != _generation || !_closing)
                {
                    if (_closeRetryGeneration == generation) _closeRetryGeneration = null;
                    return;
                }
                TryFinishClose();
                if (!_closing) { _closeRetryGeneration = null; return; }
            }
        }
        lock (_sync)
        {
            if (generation != _generation || !_closing) return;
            if (_closeRetryGeneration == generation) _closeRetryGeneration = null;
        }
        try { ReceiveError?.Invoke(new MidiException("MIDI 驱动未归还关闭中的缓冲，保留内存以等待完成通知")); } catch { }
    }

    // Called under _sync after reset. Unprepare success is the ownership boundary: never free on
    // STILLPLAYING (including a failed reset). Later completion callbacks retry retained buffers.
    private void TryFinishClose()
    {
        foreach (var (header, input) in _inputs.ToArray())
        {
            if (input.Prepared && _api.UnprepareInput(_hIn, header, HeaderSize) != WinMM.MMSYSERR_NOERROR) continue;
            _inputs.Remove(header);
            input.Dispose();
        }
        foreach (var output in _outputs.Values.ToArray()) TryReleaseOutput(output);
        if (_hIn != IntPtr.Zero && _inputs.Count == 0 && _api.CloseInput(_hIn) == WinMM.MMSYSERR_NOERROR) _hIn = IntPtr.Zero;
        if (_hOut != IntPtr.Zero && _outputs.Count == 0 && _api.CloseOutput(_hOut) == WinMM.MMSYSERR_NOERROR) _hOut = IntPtr.Zero;
        if (_hIn == IntPtr.Zero && _hOut == IntPtr.Zero)
        {
            _closing = false;
            ClosingDevices.TryRemove(this, out _);
            if (_disposed) _events.Writer.TryComplete();
        }
        else ClosingDevices.TryAdd(this, 0);
    }

    public void Dispose()
    {
        lock (_sync) _disposed = true;
        Close();
    }

    private static void Check(int error, string operation)
    {
        if (error != WinMM.MMSYSERR_NOERROR) throw new MidiException($"{operation} 失败: {error}", error);
    }
}

public sealed class MidiException : Exception
{
    public int WinMMError { get; }
    public MidiException(string message, int winMMError = 0) : base(message) => WinMMError = winMMError;
}

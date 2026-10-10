using System.Runtime.InteropServices;

namespace SonicCube.Shared.Midi;

/// <summary>
/// 一台打开的 MIDI 设备(输入 + 输出配对)。
/// 输入使用多缓冲(4×4KB)接收 SysEx;长消息回调后重新提交缓冲。
/// 回调中不执行阻塞 IO,只把字节入队由消费线程处理。
/// </summary>
public sealed class MidiDevice : IDisposable
{
    public const string DeviceNameSubstring = "Sonic Cube II";

    private const int InBufferCount = 4;
    private const int InBufferSize = 4096;

    private IntPtr _hIn;
    private IntPtr _hOut;
    private WinMM.MidiInProc? _inProc; // 保持委托存活
    private readonly object _sync = new();
    private bool _disposed;

    private IntPtr[] _inHdrMem = [];
    private IntPtr[] _inDataMem = [];
    private GCHandle[] _inDataPins = [];

    /// <summary>收到一段输入字节(SysEx 流片段)时触发。调用方负责线程编组。</summary>
    public event Action<byte[]>? DataReceived;

    public string? InName { get; private set; }
    public string? OutName { get; private set; }
    public bool IsOpen { get; private set; }

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
        dev.Open(ins[0].Index, outs[0].Index, ins[0].Name, outs[0].Name);
        return dev;
    }

    public void Open(uint inIndex, uint outIndex, string inName, string outName)
    {
        lock (_sync)
        {
            if (IsOpen) return;

            _inProc = InCallback;
            int err = WinMM.midiInOpen(out _hIn, inIndex, _inProc, IntPtr.Zero, WinMM.CALLBACK_FUNCTION);
            if (err != WinMM.MMSYSERR_NOERROR)
                throw new MidiException($"midiInOpen 失败: {WinMM.InErrorText(err)}", err);

            err = WinMM.midiOutOpen(out _hOut, outIndex, null, IntPtr.Zero, 0);
            if (err != WinMM.MMSYSERR_NOERROR)
            {
                int openErr = err;
                WinMM.midiInClose(_hIn);
                _hIn = IntPtr.Zero;
                throw new MidiException($"midiOutOpen 失败: {WinMM.OutErrorText(openErr)}", openErr);
            }

            // 准备并提交输入缓冲
            _inHdrMem = new IntPtr[InBufferCount];
            _inDataMem = new IntPtr[InBufferCount];
            _inDataPins = new GCHandle[InBufferCount];
            for (int i = 0; i < InBufferCount; i++)
            {
                var buf = new byte[InBufferSize];
                _inDataPins[i] = GCHandle.Alloc(buf, GCHandleType.Pinned);
                _inDataMem[i] = _inDataPins[i].AddrOfPinnedObject();
                _inHdrMem[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WinMM.MIDIHDR>());
                var hdr = new WinMM.MIDIHDR
                {
                    lpData = _inDataMem[i],
                    dwBufferLength = InBufferSize,
                    dwBytesRecorded = 0,
                    dwUser = (IntPtr)i,
                    dwFlags = 0,
                    lpNext = IntPtr.Zero,
                    reserved = IntPtr.Zero,
                    dwOffset = 0,
                    dwReserved = new IntPtr[8],
                };
                Marshal.StructureToPtr(hdr, _inHdrMem[i], false);
                err = WinMM.midiInPrepareHeader(_hIn, _inHdrMem[i], (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                if (err == WinMM.MMSYSERR_NOERROR)
                    err = WinMM.midiInAddBuffer(_hIn, _inHdrMem[i], (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                if (err != WinMM.MMSYSERR_NOERROR)
                {
                    Close();
                    throw new MidiException($"midiInPrepareHeader/AddBuffer 失败: {WinMM.InErrorText(err)}", err);
                }
            }

            err = WinMM.midiInStart(_hIn);
            if (err != WinMM.MMSYSERR_NOERROR)
            {
                Close();
                throw new MidiException($"midiInStart 失败: {WinMM.InErrorText(err)}", err);
            }

            InName = inName;
            OutName = outName;
            IsOpen = true;
        }
    }

    /// <summary>发送一帧完整 SysEx(以 F0 开头、F7 结尾)。分片与间隔由调用方控制。</summary>
    public void SendSysEx(byte[] frame)
    {
        lock (_sync)
        {
            if (!IsOpen || _hOut == IntPtr.Zero) throw new MidiException("MIDI 输出未打开");

            var pin = GCHandle.Alloc(frame, GCHandleType.Pinned);
            IntPtr hdrMem = IntPtr.Zero;
            try
            {
                hdrMem = Marshal.AllocHGlobal(Marshal.SizeOf<WinMM.MIDIHDR>());
                var hdr = new WinMM.MIDIHDR
                {
                    lpData = pin.AddrOfPinnedObject(),
                    dwBufferLength = (uint)frame.Length,
                    dwBytesRecorded = 0,
                    dwUser = IntPtr.Zero,
                    dwFlags = 0,
                    lpNext = IntPtr.Zero,
                    reserved = IntPtr.Zero,
                    dwOffset = 0,
                    dwReserved = new IntPtr[8],
                };
                Marshal.StructureToPtr(hdr, hdrMem, false);

                int err = WinMM.midiOutPrepareHeader(_hOut, hdrMem, (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                if (err != WinMM.MMSYSERR_NOERROR)
                    throw new MidiException($"midiOutPrepareHeader 失败: {WinMM.OutErrorText(err)}", err);
                try
                {
                    err = WinMM.midiOutLongMsg(_hOut, hdrMem, (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                    if (err != WinMM.MMSYSERR_NOERROR)
                        throw new MidiException($"midiOutLongMsg 失败: {WinMM.OutErrorText(err)}", err);
                }
                finally
                {
                    // midiOutLongMsg 同步发送 SysEx,返回后即可 unprepare。
                    WinMM.midiOutUnprepareHeader(_hOut, hdrMem, (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                }
            }
            finally
            {
                if (hdrMem != IntPtr.Zero) Marshal.FreeHGlobal(hdrMem);
                pin.Free();
            }
        }
    }

    private void InCallback(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        // 回调线程:不阻塞,尽快拷贝数据并重新提交缓冲。
        if (wMsg == WinMM.MM_MIM_LONGDATA || wMsg == WinMM.MM_MIM_DATA)
        {
            if (wMsg == WinMM.MM_MIM_LONGDATA)
            {
                IntPtr hdrMem = dwParam1;
                var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(hdrMem);
                if (hdr.dwBytesRecorded > 0 && hdr.lpData != IntPtr.Zero)
                {
                    var data = new byte[hdr.dwBytesRecorded];
                    Marshal.Copy(hdr.lpData, data, 0, data.Length);
                    DataReceived?.Invoke(data);
                }
                // 复位并重新提交缓冲
                hdr.dwBytesRecorded = 0;
                hdr.dwFlags &= ~(uint)(WinMM.MHDR_DONE);
                Marshal.StructureToPtr(hdr, hdrMem, false);
                if (!_disposed && _hIn != IntPtr.Zero)
                    WinMM.midiInAddBuffer(_hIn, hdrMem, (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
            }
            else
            {
                // 短消息(普通 MIDI),面板用不到,忽略。
            }
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            if (!IsOpen && _hIn == IntPtr.Zero && _hOut == IntPtr.Zero) return;
            IsOpen = false;

            if (_hIn != IntPtr.Zero)
            {
                WinMM.midiInStop(_hIn);
                WinMM.midiInReset(_hIn);
                for (int i = 0; i < _inHdrMem.Length; i++)
                {
                    if (_inHdrMem[i] != IntPtr.Zero)
                    {
                        WinMM.midiInUnprepareHeader(_hIn, _inHdrMem[i], (uint)Marshal.SizeOf<WinMM.MIDIHDR>());
                        Marshal.FreeHGlobal(_inHdrMem[i]);
                        _inHdrMem[i] = IntPtr.Zero;
                    }
                    if (_inDataPins[i].IsAllocated) _inDataPins[i].Free();
                }
                WinMM.midiInClose(_hIn);
                _hIn = IntPtr.Zero;
            }
            if (_hOut != IntPtr.Zero)
            {
                WinMM.midiOutReset(_hOut);
                WinMM.midiOutClose(_hOut);
                _hOut = IntPtr.Zero;
            }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Close();
    }
}

public sealed class MidiException : Exception
{
    public int WinMMError { get; }
    public MidiException(string message, int winMMError = 0) : base(message) => WinMMError = winMMError;
}

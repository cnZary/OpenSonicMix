using System.Runtime.InteropServices;

namespace SonicCube.Shared.Midi;

/// <summary>
/// WinMM(winmm.dll)MIDI 输入/输出封装,NativeAOT 兼容。
/// 依据 re/PROTOCOL.md 第 2、8 节:midiOutLongMsg 发送 SysEx,midiIn 多缓冲接收。
/// 设备按名字子串 "Sonic Cube II" 匹配,设备索引不持久化。
/// </summary>
public static partial class WinMM
{
    public const int MaxErrorLength = 256;
    public const uint CALLBACK_FUNCTION = 0x00030000;
    public const uint MIDI_MAPPER = unchecked((uint)-1);

    // midiInOpen/midiOutOpen 回调消息
    public const int MM_MIM_OPEN = 0x3C1;
    public const int MM_MIM_CLOSE = 0x3C2;
    public const int MM_MIM_DATA = 0x3C3;
    public const int MM_MIM_LONGDATA = 0x3C4;
    public const int MM_MIM_ERROR = 0x3C5;
    public const int MM_MIM_LONGERROR = 0x3C6;
    public const int MM_MOM_OPEN = 0x3C7;
    public const int MM_MOM_CLOSE = 0x3C8;
    public const int MM_MOM_DONE = 0x3C9;

    public const int MMSYSERR_NOERROR = 0;
    public const int MMSYSERR_ALLOCATED = 4;
    public const int MMSYSERR_BADDEVICEID = 2;
    public const int MMSYSERR_INVALHANDLE = 5;
    public const int MIDIERR_UNPREPARED = 0x40;
    public const int MIDIERR_STILLPLAYING = 0x41;
    public const int MIDIERR_NOTREADY = 0x43;

    public const int MHDR_DONE = 0x0001;
    public const int MHDR_PREPARED = 0x0002;
    public const int MHDR_INQUEUE = 0x0004;
    public const int MHDR_ISSTRM = 0x0008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct MIDIINCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public uint dwSupport;

        public readonly string Name
        {
            get
            {
                fixed (char* p = szPname)
                    return new string(p);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public unsafe struct MIDIOUTCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        public fixed char szPname[32];
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;

        public readonly string Name
        {
            get
            {
                fixed (char* p = szPname)
                    return new string(p);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MIDIHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint dwOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public IntPtr[] dwReserved;
    }

    [LibraryImport("winmm.dll", EntryPoint = "midiInGetNumDevs")]
    public static partial uint midiInGetNumDevs();

    [LibraryImport("winmm.dll", EntryPoint = "midiOutGetNumDevs")]
    public static partial uint midiOutGetNumDevs();

    [LibraryImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int midiInGetDevCaps(UIntPtr uDeviceID, out MIDIINCAPSW lpCaps, uint cbCaps);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutGetDevCapsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int midiOutGetDevCaps(UIntPtr uDeviceID, out MIDIOUTCAPSW lpCaps, uint cbCaps);

    public delegate void MidiInProc(IntPtr hMidiIn, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);
    public delegate void MidiOutProc(IntPtr hMidiOut, int wMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    [LibraryImport("winmm.dll", EntryPoint = "midiInOpen")]
    public static partial int midiInOpen(out IntPtr lphMidiIn, uint uDeviceID, MidiInProc dwCallback, IntPtr dwCallbackInstance, uint dwFlags);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutOpen")]
    public static partial int midiOutOpen(out IntPtr lphMidiOut, uint uDeviceID, MidiOutProc? dwCallback, IntPtr dwCallbackInstance, uint dwFlags);

    [LibraryImport("winmm.dll", EntryPoint = "midiInClose")]
    public static partial int midiInClose(IntPtr hMidiIn);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutClose")]
    public static partial int midiOutClose(IntPtr hMidiOut);

    [LibraryImport("winmm.dll", EntryPoint = "midiInPrepareHeader")]
    public static partial int midiInPrepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiInUnprepareHeader")]
    public static partial int midiInUnprepareHeader(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiInAddBuffer")]
    public static partial int midiInAddBuffer(IntPtr hMidiIn, IntPtr lpMidiInHdr, uint cbMidiInHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiInStart")]
    public static partial int midiInStart(IntPtr hMidiIn);

    [LibraryImport("winmm.dll", EntryPoint = "midiInStop")]
    public static partial int midiInStop(IntPtr hMidiIn);

    [LibraryImport("winmm.dll", EntryPoint = "midiInReset")]
    public static partial int midiInReset(IntPtr hMidiIn);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutPrepareHeader")]
    public static partial int midiOutPrepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutUnprepareHeader")]
    public static partial int midiOutUnprepareHeader(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutLongMsg")]
    public static partial int midiOutLongMsg(IntPtr hMidiOut, IntPtr lpMidiOutHdr, uint cbMidiOutHdr);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutReset")]
    public static partial int midiOutReset(IntPtr hMidiOut);

    [LibraryImport("winmm.dll", EntryPoint = "midiInGetErrorTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int midiInGetErrorText(int wError, char* lpText, uint cchText);

    [LibraryImport("winmm.dll", EntryPoint = "midiOutGetErrorTextW", StringMarshalling = StringMarshalling.Utf16)]
    public static unsafe partial int midiOutGetErrorText(int wError, char* lpText, uint cchText);

    public static unsafe string InErrorText(int err)
    {
        char* buf = stackalloc char[MaxErrorLength];
        buf[0] = '\0';
        return midiInGetErrorText(err, buf, MaxErrorLength) == 0 ? new string(buf) : $"MIDI in error {err}";
    }

    public static unsafe string OutErrorText(int err)
    {
        char* buf = stackalloc char[MaxErrorLength];
        buf[0] = '\0';
        return midiOutGetErrorText(err, buf, MaxErrorLength) == 0 ? new string(buf) : $"MIDI out error {err}";
    }
}

namespace SonicCube.Shared.Midi;

// Keep the native boundary injectable: lifecycle tests never open a real MIDI port.
internal interface IWinMmApi
{
    int OpenInput(out IntPtr handle, uint index, WinMM.MidiInProc callback, IntPtr instance);
    int OpenOutput(out IntPtr handle, uint index, WinMM.MidiOutProc callback, IntPtr instance);
    int PrepareInput(IntPtr handle, IntPtr header, uint size);
    int UnprepareInput(IntPtr handle, IntPtr header, uint size);
    int AddInputBuffer(IntPtr handle, IntPtr header, uint size);
    int StartInput(IntPtr handle);
    int StopInput(IntPtr handle);
    int ResetInput(IntPtr handle);
    int CloseInput(IntPtr handle);
    int PrepareOutput(IntPtr handle, IntPtr header, uint size);
    int UnprepareOutput(IntPtr handle, IntPtr header, uint size);
    int SendOutput(IntPtr handle, IntPtr header, uint size);
    int ResetOutput(IntPtr handle);
    int CloseOutput(IntPtr handle);
}

internal sealed class WinMmApi : IWinMmApi
{
    public int OpenInput(out IntPtr h, uint i, WinMM.MidiInProc cb, IntPtr state) => WinMM.midiInOpen(out h, i, cb, state, WinMM.CALLBACK_FUNCTION);
    public int OpenOutput(out IntPtr h, uint i, WinMM.MidiOutProc cb, IntPtr state) => WinMM.midiOutOpen(out h, i, cb, state, WinMM.CALLBACK_FUNCTION);
    public int PrepareInput(IntPtr h, IntPtr b, uint s) => WinMM.midiInPrepareHeader(h, b, s);
    public int UnprepareInput(IntPtr h, IntPtr b, uint s) => WinMM.midiInUnprepareHeader(h, b, s);
    public int AddInputBuffer(IntPtr h, IntPtr b, uint s) => WinMM.midiInAddBuffer(h, b, s);
    public int StartInput(IntPtr h) => WinMM.midiInStart(h);
    public int StopInput(IntPtr h) => WinMM.midiInStop(h);
    public int ResetInput(IntPtr h) => WinMM.midiInReset(h);
    public int CloseInput(IntPtr h) => WinMM.midiInClose(h);
    public int PrepareOutput(IntPtr h, IntPtr b, uint s) => WinMM.midiOutPrepareHeader(h, b, s);
    public int UnprepareOutput(IntPtr h, IntPtr b, uint s) => WinMM.midiOutUnprepareHeader(h, b, s);
    public int SendOutput(IntPtr h, IntPtr b, uint s) => WinMM.midiOutLongMsg(h, b, s);
    public int ResetOutput(IntPtr h) => WinMM.midiOutReset(h);
    public int CloseOutput(IntPtr h) => WinMM.midiOutClose(h);
}

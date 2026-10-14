using System.Buffers.Binary;

namespace SonicCube.Protocol.Models;

/// <summary>
/// 整包预设(0x00000010)响应解析。
/// 布局(按字节):inverseSw(2)→hpfSw(2)→lowCut(2)→loopbackSw(2)→dspRecSw(1)→fxModuleEn(1)
/// →fxSlot(6)→fxAlg(6×int32)→fxParaVal(6×16×float32)→muteSw(4)→soloSw(2)→reserve(2)
/// →volPot(4×float32)→phase(2×float32)→send(2×float32)→monoSw(2,仅取首字节)。
/// 单项解析按 int32 LE;整包中 fxAlg/fxParaVal/volPot/phase/send 为 4 字节小端值,其余为 1 字节。
/// </summary>
public sealed class PresetState
{
    public const int ModuleCount = 6;      // 槽位 0..5(CAB 无 JSON 定义)
    public const int ParamsPerModule = 16;

    public int[] InverseSw { get; } = new int[2];
    public int[] HpfSw { get; } = new int[2];
    public int[] LowCut { get; } = new int[2];       // 0=75Hz, 1=100Hz
    public int[] LoopbackSw { get; } = new int[2];   // 0=OTG, 1=USB
    public int DspRecSw { get; set; }                // 1=dsprec, 0=dspmon
    public int FxModuleEn { get; set; }              // bit0..5
    public int[] FxSlot { get; } = new int[ModuleCount];
    public int[] FxAlg { get; } = new int[ModuleCount];
    public float[] FxParaVal { get; } = new float[ModuleCount * ParamsPerModule];
    public int[] MuteSw { get; } = new int[4];
    public int[] SoloSw { get; } = new int[2];
    public float[] VolPot { get; } = new float[4];
    public float[] Phase { get; } = new float[2];
    public float[] Send { get; } = new float[2];
    public int MonoSw { get; set; }

    /// <summary>解析整包预设响应。data 为消息载荷 data 段(不含 8 字节头)。</summary>
    public static PresetState Parse(ReadOnlySpan<byte> data)
    {
        var s = new PresetState();
        int p = 0;
        CopyBytes(data, ref p, s.InverseSw, 2);
        CopyBytes(data, ref p, s.HpfSw, 2);
        CopyBytes(data, ref p, s.LowCut, 2);
        CopyBytes(data, ref p, s.LoopbackSw, 2);
        s.DspRecSw = ReadByte(data, ref p);
        s.FxModuleEn = ReadByte(data, ref p);
        CopyBytes(data, ref p, s.FxSlot, ModuleCount);
        for (int i = 0; i < ModuleCount; i++) s.FxAlg[i] = ReadInt32LE(data, ref p);
        for (int i = 0; i < s.FxParaVal.Length; i++) s.FxParaVal[i] = ReadSingleLE(data, ref p);
        CopyBytes(data, ref p, s.MuteSw, 4);
        CopyBytes(data, ref p, s.SoloSw, 2);
        p += 2; // reserve
        for (int i = 0; i < 4; i++) s.VolPot[i] = ReadSingleLE(data, ref p);
        for (int i = 0; i < 2; i++) s.Phase[i] = ReadSingleLE(data, ref p);
        for (int i = 0; i < 2; i++) s.Send[i] = ReadSingleLE(data, ref p);
        s.MonoSw = ReadByte(data, ref p); // 占 2 字节槽,仅取首字节
        return s;
    }

    /// <summary>按单项响应更新对应字段(地址低 24 位 = 类型地址)。</summary>
    public void ApplyIndividual(uint typeAddress, int location, ReadOnlySpan<byte> data)
    {
        switch (typeAddress)
        {
            case Commands.CommandAddress.Inverse:
                if (InRange(location, 2)) InverseSw[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.HpfSwitch:
                if (InRange(location, 2)) HpfSw[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.LowCut:
                if (InRange(location, 2)) LowCut[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.FxModuleEnable:
                // 实测(2026-10,真机 fx 诊断):该地址写入的确认回包载荷恒为 0,不是新位图;
                // 设备实际语义是按 location 置位/清位。位图由写入方合并(SetModuleEnabled)
                // 与整包预设读回维护,此处绝不可用回包载荷整图覆盖(否则一次写入就清空所有模块开关)。
                break;
            case Commands.CommandAddress.FxSlot:
                if (InRange(location, ModuleCount)) FxSlot[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.FxAlgorithm:
                if (InRange(location, ModuleCount)) FxAlg[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.FxParameter:
                if (data.Length >= 12)
                {
                    int module = BinaryPrimitives.ReadInt32LittleEndian(data);
                    int idx = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(4));
                    float v = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(8));
                    int flat = module * ParamsPerModule + idx;
                    if ((uint)flat < (uint)FxParaVal.Length) FxParaVal[flat] = v;
                }
                break;
            case Commands.CommandAddress.MuteSwitch:
                if (InRange(location, 4)) MuteSw[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.SoloSwitch:
                if (InRange(location, 2)) SoloSw[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.VolumePot:
                if (InRange(location, 4)) VolPot[location] = PayloadReadFloat(data); break;
            case Commands.CommandAddress.Phase:
                if (InRange(location, 2)) Phase[location] = PayloadReadFloat(data); break;
            case Commands.CommandAddress.Send:
                if (InRange(location, 2)) Send[location] = PayloadReadFloat(data); break;
            case Commands.CommandAddress.Loopback:
                if (InRange(location, 2)) LoopbackSw[location] = PayloadReadInt(data); break;
            case Commands.CommandAddress.DspRecSwitch:
                DspRecSw = PayloadReadInt(data); break;
            case Commands.CommandAddress.Mono:
                MonoSw = PayloadReadInt(data); break;
        }
    }

    /// <summary>读取某个 fxParaVal(module*16+paramIndex)。</summary>
    public float GetFxPara(int module, int paramIndex)
    {
        int flat = module * ParamsPerModule + paramIndex;
        return (uint)flat < (uint)FxParaVal.Length ? FxParaVal[flat] : 0f;
    }

    /// <summary>模块是否使能(bit)。</summary>
    public bool IsModuleEnabled(int module) => ((FxModuleEn >> module) & 1) == 1;

    /// <summary>按写入值本地合并模块使能位(写入路径专用;设备语义 = 按 location 置位/清位)。</summary>
    public void SetModuleEnabled(int module, bool enabled)
    {
        if ((uint)module >= (uint)ModuleCount) return;
        if (enabled) FxModuleEn |= 1 << module;
        else FxModuleEn &= ~(1 << module);
    }

    private static bool InRange(int i, int n) => (uint)i < (uint)n;

    private static int PayloadReadInt(ReadOnlySpan<byte> d) =>
        d.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(d) : 0;

    private static float PayloadReadFloat(ReadOnlySpan<byte> d) =>
        d.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(d) : 0f;

    private static void CopyBytes(ReadOnlySpan<byte> data, ref int p, int[] dst, int count)
    {
        for (int i = 0; i < count; i++) dst[i] = p < data.Length ? data[p++] : 0;
    }

    private static int ReadByte(ReadOnlySpan<byte> data, ref int p) => p < data.Length ? data[p++] : 0;

    private static int ReadInt32LE(ReadOnlySpan<byte> data, ref int p)
    {
        int v = p + 4 <= data.Length ? BinaryPrimitives.ReadInt32LittleEndian(data.Slice(p)) : 0;
        p += 4;
        return v;
    }

    private static float ReadSingleLE(ReadOnlySpan<byte> data, ref int p)
    {
        float v = p + 4 <= data.Length ? BinaryPrimitives.ReadSingleLittleEndian(data.Slice(p)) : 0f;
        p += 4;
        return v;
    }
}

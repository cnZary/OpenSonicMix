namespace SonicCube.Protocol.Models;

/// <summary>
/// 全部全局参数(0x00000011)响应解析。
/// 响应 data[0..7]:hiEn, 48V, efx, rvbOn, monitor, onAir, mute, autoOutput(各 1 字节)。
/// </summary>
public sealed class GlobalState
{
    public int HiEn { get; set; }
    public int Phantom48V { get; set; }
    public int Efx { get; set; }
    public int ReverbOn { get; set; }
    public int Monitor { get; set; }
    public int OnAir { get; set; }
    public int MasterMute { get; set; }
    public int AutoOutput { get; set; }

    public static GlobalState Parse(ReadOnlySpan<byte> data)
    {
        return new GlobalState
        {
            HiEn = ByteAt(data, 0),
            Phantom48V = ByteAt(data, 1),
            Efx = ByteAt(data, 2),
            ReverbOn = ByteAt(data, 3),
            Monitor = ByteAt(data, 4),
            OnAir = ByteAt(data, 5),
            MasterMute = ByteAt(data, 6),
            AutoOutput = ByteAt(data, 7),
        };
    }

    private static int ByteAt(ReadOnlySpan<byte> data, int i) => i < data.Length ? data[i] : 0;

    /// <summary>按单项全局响应更新(地址低 24 位 = 类型地址)。</summary>
    public void ApplyIndividual(uint typeAddress, int value)
    {
        switch (typeAddress)
        {
            case Commands.CommandAddress.Hi: HiEn = value; break;
            case Commands.CommandAddress.Phantom48V: Phantom48V = value; break;
            case Commands.CommandAddress.Efx: Efx = value; break;
            case Commands.CommandAddress.ReverbOn: ReverbOn = value; break;
            case Commands.CommandAddress.Monitor: Monitor = value; break;
            case Commands.CommandAddress.OnAir: OnAir = value; break;
            case Commands.CommandAddress.MasterMute: MasterMute = value; break;
            case Commands.CommandAddress.AutoOutput: AutoOutput = value; break;
        }
    }

    /// <summary>解析电平包(0x000B0011):10 × float32 dB。通道 0/1=MIC1, 2/3=LINE/INST2, 4/5=USB, 6/7=OTG, 8/9=主输出。</summary>
    public static float[] ParseMeters(ReadOnlySpan<byte> data)
    {
        var m = new float[10];
        for (int i = 0; i < 10 && i * 4 + 4 <= data.Length; i++)
            m[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(data.Slice(i * 4));
        return m;
    }
}

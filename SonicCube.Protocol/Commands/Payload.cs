using System.Buffers.Binary;

namespace SonicCube.Protocol.Commands;

/// <summary>
/// 读写载荷构建与解析(见 re/PROTOCOL.md 第 5 节)。
/// int32/float32 均为小端;写效果参数为 module+paramIdx+float32 共 12 字节。
/// </summary>
public static class Payload
{
    /// <summary>构建 4 字节 int32 写载荷。</summary>
    public static byte[] Int32(int value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, value);
        return b;
    }

    /// <summary>构建 4 字节 float32 写载荷。</summary>
    public static byte[] Float32(float value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(b, value);
        return b;
    }

    /// <summary>构建 12 字节效果参数写载荷(module + paramIdx + float32)。</summary>
    public static byte[] FxParameter(int module, int paramIndex, float value)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0, 4), module);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4, 4), paramIndex);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8, 4), value);
        return b;
    }

    public static int ReadInt32(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(data) : 0;

    public static float ReadFloat32(ReadOnlySpan<byte> data) =>
        data.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(data) : 0f;
}

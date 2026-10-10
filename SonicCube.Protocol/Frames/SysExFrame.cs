using System.Buffers.Binary;

namespace SonicCube.Protocol.Frames;

/// <summary>帧标志:请求(等回复)。</summary>
public enum FrameFlag : byte
{
    /// <summary>请求(等回复)。</summary>
    Request = 0x11,
    /// <summary>设置(发送)。</summary>
    Set = 0x12,
}

/// <summary>
/// SysEx 帧编解码(见 re/PROTOCOL.md 第 3 节)。
/// 线上帧:F0 | "!%~JUSH"(7B) | flag | totalLen(2B 7bit LE) | offset(2B 7bit LE) | payload(nibble) | F7。
/// 消息载荷:[cmdAddr(4B BE)][dataLen(4B LE)][data]。每片最多 183 字节原始载荷,无校验和。
/// </summary>
public static class SysExFrame
{
    /// <summary>magic "!%~JUSH"。</summary>
    public static ReadOnlySpan<byte> Magic => "!\x25~JUSH"u8;

    public const int HeaderSize = 8;      // 4B cmdAddr + 4B dataLen
    public const int MaxChunkPayload = 183; // 每片最大原始载荷

    /// <summary>构建一条完整消息载荷(未分片):cmdAddr(4B BE) + dataLen(4B LE) + data。</summary>
    public static byte[] BuildMessagePayload(uint cmdAddr, ReadOnlySpan<byte> data)
    {
        var payload = new byte[HeaderSize + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), cmdAddr);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)data.Length);
        data.CopyTo(payload.AsSpan(HeaderSize));
        return payload;
    }

    /// <summary>
    /// 将一条消息载荷编码为一帧或多帧 SysEx。每帧 ≤183 字节原始载荷,offset 累加。
    /// </summary>
    public static byte[][] Encode(uint cmdAddr, FrameFlag flag, ReadOnlySpan<byte> data)
    {
        var payload = BuildMessagePayload(cmdAddr, data);
        int total = payload.Length;
        int frameCount = Math.Max(1, (total + MaxChunkPayload - 1) / MaxChunkPayload);
        var frames = new byte[frameCount][];

        int offset = 0;
        for (int i = 0; i < frameCount; i++)
        {
            int chunkLen = Math.Min(MaxChunkPayload, total - offset);
            frames[i] = EncodeSingleFrame(flag, total, offset, payload.AsSpan(offset, chunkLen));
            offset += chunkLen;
        }
        return frames;
    }

    /// <summary>编码单帧。</summary>
    public static byte[] EncodeSingleFrame(FrameFlag flag, int totalLen, int offset, ReadOnlySpan<byte> chunk)
    {
        // F0(1) + magic(7) + flag(1) + totalLen(2) + offset(2) + nibble(chunk*2) + F7(1)
        var frame = new byte[1 + 7 + 1 + 2 + 2 + chunk.Length * 2 + 1];
        int p = 0;
        frame[p++] = 0xF0;
        Magic.CopyTo(frame.AsSpan(p)); p += 7;
        frame[p++] = (byte)flag;
        frame[p++] = (byte)(totalLen & 0x7F);
        frame[p++] = (byte)((totalLen >> 7) & 0x7F);
        frame[p++] = (byte)(offset & 0x7F);
        frame[p++] = (byte)((offset >> 7) & 0x7F);
        foreach (byte b in chunk)
        {
            frame[p++] = (byte)(b >> 4);    // 高 4 位先发
            frame[p++] = (byte)(b & 0x0F);  // 低 4 位后发
        }
        frame[p] = 0xF7;
        return frame;
    }

    /// <summary>
    /// 增量式 SysEx 流解析器:接收任意字节流,缓冲 F0..F7 帧,完成时输出重组后的消息。
    /// 线程安全:仅在单线程(回调泵)使用;内部缓冲复用。
    /// </summary>
    public sealed class Parser
    {
        private readonly List<byte> _frameBuf = new();
        private bool _inFrame;

        // 重组状态
        private int _totalLen;
        private readonly byte[] _reassembleBuf = new byte[64 * 1024];
        private int _receivedLen;
        private bool _hasFrame;

        /// <summary>喂入接收到的字节,返回重组完成的消息数(消息经 out 交付)。</summary>
        public int Feed(ReadOnlySpan<byte> bytes, List<(uint CmdAddr, byte[] Data)> completed)
        {
            int before = completed.Count;
            foreach (byte b in bytes)
            {
                if (!_inFrame)
                {
                    if (b == 0xF0)
                    {
                        _inFrame = true;
                        _frameBuf.Clear();
                        _frameBuf.Add(b);
                    }
                    continue;
                }

                _frameBuf.Add(b);
                if (b == 0xF7)
                {
                    TryConsumeFrame(completed);
                    _inFrame = false;
                    _frameBuf.Clear();
                }
                else if (_frameBuf.Count > 8192) // 防御:异常长帧
                {
                    _inFrame = false;
                    _frameBuf.Clear();
                    ResetReassembly();
                }
            }
            return completed.Count - before;
        }

        private void TryConsumeFrame(List<(uint, byte[])> completed)
        {
            // 最小帧:F0 + magic(7) + flag(1) + total(2) + offset(2) + F7 = 14
            if (_frameBuf.Count < 14) return;
            var f = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_frameBuf);
            if (!f.Slice(1, 7).SequenceEqual(Magic)) return; // 非 JUSH 帧(IUSH/固件/其他),忽略

            int totalLen = f[9] | (f[10] << 7);
            int offset = f[11] | (f[12] << 7);
            int nibbleBytes = _frameBuf.Count - 14; // 去掉 F0..offset 与 F7
            if ((nibbleBytes & 1) != 0) return;
            int chunkLen = nibbleBytes / 2;
            if (offset + chunkLen > totalLen || totalLen > _reassembleBuf.Length) { ResetReassembly(); return; }

            // 解 nibble
            for (int i = 0; i < chunkLen; i++)
            {
                byte hi = f[13 + i * 2];
                byte lo = f[13 + i * 2 + 1];
                if (hi > 0x0F || lo > 0x0F) { ResetReassembly(); return; }
                _reassembleBuf[offset + i] = (byte)(hi << 4 | lo);
            }

            if (offset == 0)
            {
                _receivedLen = 0;
                _totalLen = totalLen;
                _hasFrame = true;
            }
            else if (!_hasFrame || totalLen != _totalLen)
            {
                ResetReassembly();
                return;
            }
            _receivedLen = Math.Max(_receivedLen, offset + chunkLen);

            if (_receivedLen >= _totalLen && _totalLen >= HeaderSize)
            {
                uint cmdAddr = BinaryPrimitives.ReadUInt32BigEndian(_reassembleBuf.AsSpan(0, 4));
                int dataLen = BinaryPrimitives.ReadInt32LittleEndian(_reassembleBuf.AsSpan(4, 8));
                if (dataLen < 0 || HeaderSize + dataLen > _totalLen) { ResetReassembly(); return; }
                var data = _reassembleBuf.AsSpan(HeaderSize, dataLen).ToArray();
                completed.Add((cmdAddr, data));
                ResetReassembly();
            }
        }

        private void ResetReassembly()
        {
            _hasFrame = false;
            _receivedLen = 0;
            _totalLen = 0;
        }

        public void Reset()
        {
            _frameBuf.Clear();
            _inFrame = false;
            ResetReassembly();
        }
    }
}

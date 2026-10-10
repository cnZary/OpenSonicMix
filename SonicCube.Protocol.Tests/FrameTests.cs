using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Frames;
using SonicCube.Protocol.Models;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class FrameTests
{
    // PROTOCOL.md 8.5 工作示例:请求 0x00010013、空载荷、flag=0x11 的线上帧。
    [TestMethod]
    public void Encode_SoftwareStateRequest_MatchesKnownBytes()
    {
        var frames = SysExFrame.Encode(0x00010013, FrameFlag.Request, []);
        Assert.AreEqual(1, frames.Length);
        byte[] expected =
        [
            0xF0, 0x21, 0x25, 0x7E, 0x4A, 0x55, 0x53, 0x48,
            0x11,
            0x08, 0x00,
            0x00, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x01, 0x03,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xF7,
        ];
        CollectionAssert.AreEqual(expected, frames[0]);
    }

    [TestMethod]
    public void EncodeDecode_RoundTrip_Int32()
    {
        var frames = SysExFrame.Encode(0x00020011, FrameFlag.Set, Payload.Int32(1));
        var parser = new SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        foreach (var f in frames) parser.Feed(f, done);
        Assert.AreEqual(1, done.Count);
        Assert.AreEqual(0x00020011u, done[0].Item1);
        Assert.AreEqual(1, Payload.ReadInt32(done[0].Item2));
    }

    [TestMethod]
    public void Encode_MultiFrame_WhenPayloadExceedsChunk()
    {
        // 载荷 > 183 字节 → 多片。fxParaVal 大小的数据足以触发。
        var data = new byte[400];
        new Random(42).NextBytes(data);
        var frames = SysExFrame.Encode(0x00000010, FrameFlag.Set, data);
        // 总载荷 = 8 + 400 = 408 → 3 片(183+183+42)
        Assert.AreEqual(3, frames.Length);

        var parser = new SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        foreach (var f in frames) parser.Feed(f, done);
        Assert.AreEqual(1, done.Count);
        CollectionAssert.AreEqual(data, done[0].Item2);
    }

    [TestMethod]
    public void Parser_HandlesSplitDelivery_AcrossFeeds()
    {
        var frames = SysExFrame.Encode(0x00070011, FrameFlag.Set, Payload.Int32(0));
        var parser = new SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        // 逐字节喂入,模拟流式到达
        foreach (var f in frames)
            foreach (byte b in f)
                parser.Feed([b], done);
        Assert.AreEqual(1, done.Count);
        Assert.AreEqual(0x00070011u, done[0].Item1);
    }

    [TestMethod]
    public void Parser_ReassemblesInterleavedChunks_WhenSplitMidFrame()
    {
        var data = new byte[300];
        new Random(7).NextBytes(data);
        var frames = SysExFrame.Encode(0x00000010, FrameFlag.Request, data);
        var parser = new SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        // 把所有帧拼接后切成任意小块喂入
        var all = frames.SelectMany(f => f).ToArray();
        for (int i = 0; i < all.Length; i += 50)
            parser.Feed(all.AsSpan(i, Math.Min(50, all.Length - i)), done);
        Assert.AreEqual(1, done.Count);
        CollectionAssert.AreEqual(data, done[0].Item2);
    }

    [TestMethod]
    public void Parser_IgnoresNonJushFrames()
    {
        // IUSH 帧(magic 不同)应被忽略,不应产出消息。
        byte[] iush = [0xF0, 0x21, 0x25, 0x7E, 0x49, 0x55, 0x53, 0x48, 0x11, 0x08, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0xF7];
        var parser = new SysExFrame.Parser();
        var done = new List<(uint, byte[])>();
        parser.Feed(iush, done);
        Assert.AreEqual(0, done.Count);
    }

    [TestMethod]
    public void Payload_FxParameter_Is12BytesLittleEndian()
    {
        var b = Payload.FxParameter(3, 5, 0.5f);
        Assert.AreEqual(12, b.Length);
        Assert.AreEqual(3, BitConverter.ToInt32(b, 0));
        Assert.AreEqual(5, BitConverter.ToInt32(b, 4));
        Assert.AreEqual(0.5f, BitConverter.ToSingle(b, 8));
    }

    [TestMethod]
    public void CommandAddress_Location_RoundTrip()
    {
        uint full = CommandAddress.WithLocation(CommandAddress.MuteSwitch, 3);
        Assert.AreEqual(0x03090010u, full);
        Assert.AreEqual(3, CommandAddress.LocationOf(full));
        Assert.AreEqual(CommandAddress.MuteSwitch, CommandAddress.TypeOf(full));
    }
}

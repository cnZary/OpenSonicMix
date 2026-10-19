using System.Text;
using SonicCube.Shared.Ipc;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class IpcFramingTests
{
    [TestMethod]
    public void TryReadLine_ParsesUtf8AndCrLf_ThenPreservesNextMessage()
    {
        var buffer = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"errorDetail\":\"设备未连接 🎚️\"}\r\n{\"type\":\"meter_update\",\"meters\":[-100,-6.5,0]}\n");
        int length = buffer.Length;

        Assert.IsTrue(IpcFraming.TryReadLine(buffer, ref length, out var first, out var error));
        Assert.IsNull(error);
        Assert.AreEqual("设备未连接 🎚️", first!.ErrorDetail);
        Assert.IsTrue(IpcFraming.TryReadLine(buffer, ref length, out var second, out error));
        Assert.IsNull(error);
        Assert.AreEqual(IpcMessageType.MeterUpdate, second!.Type);
        CollectionAssert.AreEqual(new float[] { -100, -6.5f, 0 }, second.Meters);
        Assert.AreEqual(0, length);
    }

    [TestMethod]
    public void TryReadLine_IncompleteUtf8Delivery_WaitsForNewlineWithoutChangingBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"error\",\"errorDetail\":\"设备\"}\n");
        var buffer = new byte[bytes.Length];
        int length = 0;

        for (int i = 0; i < bytes.Length - 1; i++)
        {
            buffer[length++] = bytes[i];
            Assert.IsFalse(IpcFraming.TryReadLine(buffer, ref length, out var incomplete, out var error));
            Assert.IsNull(incomplete);
            Assert.IsNull(error);
            Assert.AreEqual(i + 1, length);
            CollectionAssert.AreEqual(bytes[..length], buffer[..length]);
        }

        buffer[length++] = bytes[^1];
        Assert.IsTrue(IpcFraming.TryReadLine(buffer, ref length, out var msg, out var finalError));
        Assert.IsNull(finalError);
        Assert.AreEqual("设备", msg!.ErrorDetail);
        Assert.AreEqual(0, length);
    }

    [TestMethod]
    public void TryReadLine_InvalidJson_ConsumesOnlyInvalidLine()
    {
        var buffer = Encoding.UTF8.GetBytes("{broken}\n{\"type\":\"get_state\"}\n");
        int length = buffer.Length;

        Assert.IsFalse(IpcFraming.TryReadLine(buffer, ref length, out var invalid, out var error));
        Assert.IsNull(invalid);
        Assert.IsNotNull(error);
        Assert.IsTrue(IpcFraming.TryReadLine(buffer, ref length, out var valid, out error));
        Assert.IsNull(error);
        Assert.AreEqual(IpcMessageType.GetState, valid!.Type);
        Assert.AreEqual(0, length);
    }

    [TestMethod]
    public void TryReadLine_EmptyCrLf_ConsumesOnlyEmptyLine()
    {
        var buffer = Encoding.UTF8.GetBytes("\r\n{\"type\":\"get_settings\"}\n");
        int length = buffer.Length;

        Assert.IsFalse(IpcFraming.TryReadLine(buffer, ref length, out var empty, out var error));
        Assert.IsNull(empty);
        Assert.IsNull(error);
        Assert.IsTrue(IpcFraming.TryReadLine(buffer, ref length, out var msg, out error));
        Assert.IsNull(error);
        Assert.AreEqual(IpcMessageType.GetSettings, msg!.Type);
        Assert.AreEqual(0, length);
    }
}

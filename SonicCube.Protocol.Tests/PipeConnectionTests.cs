using System.IO.Pipes;
using SonicCube.Shared.Ipc;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class PipeConnectionTests
{
    [TestMethod]
    public async Task CancelledQueuedMessageIsNotWritten()
    {
        string name = "soniccube-pipe-test-" + Guid.NewGuid().ToString("N");
        using var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(3000);
        await accept;
        using var server = new PipeConnection(serverStream);
        using var client = new PipeConnection(clientStream);
        using var cancel = new CancellationTokenSource();
        var discarded = client.SendAsync(new IpcMessage { Type = "discard" }, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await discarded);
        var received = new TaskCompletionSource<IpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += msg => received.TrySetResult(msg);
        server.StartReadLoop();
        client.StartReadLoop();
        await client.SendAsync(new IpcMessage { Type = "keep" });
        Assert.AreEqual("keep", (await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Type);
    }

    [TestMethod]
    public async Task DisposeCompletesWritesQueuedBehindBlockedPeer()
    {
        string name = "soniccube-pipe-test-" + Guid.NewGuid().ToString("N");
        using var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);
        using var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(3000);
        await accept;
        using var client = new PipeConnection(clientStream);
        client.StartReadLoop();
        var sends = Enumerable.Range(0, 12).Select(_ => client.SendAsync(new IpcMessage { Type = "test", ErrorDetail = new string('x', 60000) })).ToArray();
        client.Dispose();
        foreach (var send in sends)
        {
            try { await send.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            Assert.IsTrue(send.IsCompleted);
        }
    }
}

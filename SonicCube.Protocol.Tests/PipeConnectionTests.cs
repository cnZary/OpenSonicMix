using System.IO.Pipes;
using SonicCube.Shared.Ipc;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class PipeConnectionTests
{
    [TestMethod]
    public async Task QueuedMetersKeepOnlyLatestWithoutReorderingReliableMessages()
    {
        string name = "soniccube-pipe-test-" + Guid.NewGuid().ToString("N");
        using var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(3000);
        await accept;
        using var server = new PipeConnection(serverStream);
        using var client = new PipeConnection(clientStream);
        var messages = new List<IpcMessage>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.MessageReceived += msg =>
        {
            messages.Add(msg);
            if (msg.Type == "end") completed.TrySetResult();
        };

        var first = client.SendAsync(new IpcMessage { Type = "first" });
        for (int i = 0; i < 10000; i++)
        {
            client.Post(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = [i] });
            if (i == 5000) client.Post(new IpcMessage { Type = "middle" });
        }
        var last = client.SendAsync(new IpcMessage { Type = "end" });
        server.StartReadLoop();
        client.StartReadLoop();
        await Task.WhenAll(first, last, completed.Task).WaitAsync(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(new[] { "first", IpcMessageType.MeterUpdate, "middle", "end" }, messages.Select(m => m.Type).ToArray());
        Assert.AreEqual(9999f, messages[1].Meters![0]);
    }

    [TestMethod]
    public async Task BlockedPipeDropsObsoleteMetersAndSendsFinalValue()
    {
        string name = "soniccube-pipe-test-" + Guid.NewGuid().ToString("N");
        using var serverStream = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1024, 1024);
        using var clientStream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = serverStream.WaitForConnectionAsync();
        await clientStream.ConnectAsync(3000);
        await accept;
        using var server = new PipeConnection(serverStream);
        using var client = new PipeConnection(clientStream);
        server.StartReadLoop();
        var blocked = server.SendAsync(new IpcMessage { Type = "blocked", ErrorDetail = new string('x', 60000) });
        await Task.Delay(100);
        Assert.IsFalse(blocked.IsCompleted, "The unread pipe must apply backpressure before meter updates are queued.");
        for (int i = 0; i < 10000; i++)
            server.Post(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = [i] });
        var reliable = server.SendAsync(new IpcMessage { Type = "command" });
        server.Post(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = [-12f] });
        var last = server.SendAsync(new IpcMessage { Type = "end" });

        var messages = new List<IpcMessage>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.MessageReceived += msg =>
        {
            messages.Add(msg);
            if (msg.Type == "end") completed.TrySetResult();
        };
        client.StartReadLoop();
        await Task.WhenAll(blocked, reliable, last, completed.Task).WaitAsync(TimeSpan.FromSeconds(3));

        CollectionAssert.AreEqual(new[] { "blocked", IpcMessageType.MeterUpdate, "command", "end" }, messages.Select(m => m.Type).ToArray());
        Assert.AreEqual(-12f, messages[1].Meters![0], "Resuming a reader must deliver the latest level, not replay the obsolete ramp.");
    }

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
        for (int i = 0; i < 10000; i++)
            client.Post(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = [i] });
        int disconnected = 0;
        client.Disconnected += () => Interlocked.Increment(ref disconnected);
        client.Dispose();
        foreach (var send in sends)
        {
            try { await send.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
            Assert.IsTrue(send.IsCompleted);
        }
        client.Post(new IpcMessage { Type = IpcMessageType.MeterUpdate, Meters = [-12f] });
        client.Dispose();
        Assert.AreEqual(1, disconnected);
    }
}

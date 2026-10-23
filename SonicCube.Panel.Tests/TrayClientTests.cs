using System.IO.Pipes;
using SonicCube.Panel.Ipc;
using SonicCube.Shared.Ipc;

namespace SonicCube.Panel.Tests;

[TestClass]
public sealed class TrayClientTests
{
    [TestMethod]
    public async Task DisconnectCompletesPendingRequestPromptly()
    {
        string name = "soniccube-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        using var client = new TrayClient(name);
        Assert.IsTrue(await client.ConnectAsync());
        await connected;
        using var connection = new PipeConnection(server);
        var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.MessageReceived += _ => requestSeen.TrySetResult();
        connection.StartReadLoop();
        var request = client.SetFloatAsync(123, 0.5f);
        await requestSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        connection.Dispose();
        var result = await request.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(result?.Success);
        Assert.AreEqual("disconnected", result?.ErrorCode);
    }

    [TestMethod]
    public async Task OlderRequestSnapshotCannotOverwriteNewerBroadcast()
    {
        string name = "soniccube-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        using var client = new TrayClient(name);
        Assert.IsTrue(await client.ConnectAsync());
        await connected;
        using var connection = new PipeConnection(server);
        connection.MessageReceived += msg =>
        {
            connection.Post(new IpcMessage { Type = IpcMessageType.StateChanged, Version = 8, State = new DeviceStateDto { Connected = true, Efx = 1 } });
            connection.Post(new IpcMessage { Type = IpcMessageType.CommandResult, RequestId = msg.RequestId, Version = 7, State = new DeviceStateDto { Connected = true, Efx = 0 } });
        };
        connection.StartReadLoop();
        var state = await client.GetStateDirectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(1, state?.Efx);
        Assert.AreEqual(8L, client.StateVersion);
    }

    [TestMethod]
    public async Task OlderStatusCannotReenableDisconnectedDevice()
    {
        string name = "soniccube-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var connected = server.WaitForConnectionAsync();
        using var client = new TrayClient(name);
        Assert.IsTrue(await client.ConnectAsync());
        await connected;
        using var connection = new PipeConnection(server);
        var statuses = new List<bool>();
        client.ConnectionChanged += statuses.Add;
        connection.MessageReceived += msg =>
        {
            connection.Post(new IpcMessage { Type = IpcMessageType.DeviceStatusChanged, Version = 9,
                State = new DeviceStateDto { Connected = false }, DeviceStatus = "disconnected" });
            connection.Post(new IpcMessage { Type = IpcMessageType.DeviceStatusChanged, Version = 8,
                State = new DeviceStateDto { Connected = true }, DeviceStatus = "connected" });
            connection.Post(new IpcMessage { Type = IpcMessageType.CommandResult, RequestId = msg.RequestId,
                Version = 7, State = new DeviceStateDto { Connected = true } });
        };
        connection.StartReadLoop();
        var state = await client.GetStateDirectAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsFalse(state?.Connected);
        Assert.AreEqual(9L, client.StateVersion);
        CollectionAssert.AreEqual(new[] { false }, statuses);
    }

    [TestMethod]
    public async Task ReconnectAcceptsFreshSessionVersionAndClearsOldState()
    {
        string name = "soniccube-test-" + Guid.NewGuid().ToString("N");
        using var client = new TrayClient(name);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.TrayDisconnected += () => disconnected.TrySetResult();
        for (int pass = 0; pass < 2; pass++)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accepted = server.WaitForConnectionAsync();
            Assert.IsTrue(await client.ConnectAsync());
            await accepted;
            Assert.IsNull(client.State);
            using var connection = new PipeConnection(server);
            long version = pass == 0 ? 50 : 1;
            int efx = pass;
            connection.MessageReceived += msg => connection.Post(new IpcMessage
            {
                Type = IpcMessageType.CommandResult, RequestId = msg.RequestId, Version = version,
                State = new DeviceStateDto { Connected = true, Efx = efx }, Success = true,
            });
            connection.StartReadLoop();
            var state = await client.GetStateDirectAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual(efx, state?.Efx);
            Assert.AreEqual(version, client.StateVersion);
            connection.Dispose();
            if (pass == 0) await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
}

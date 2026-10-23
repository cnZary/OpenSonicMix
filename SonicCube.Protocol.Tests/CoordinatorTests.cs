using System.Reflection;
using SonicCube.Shared.Midi;
using SonicCube.Tray.Device;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class CoordinatorTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task RepeatedToggleClicks_AreBothRetainedInOrderUntilCancelled()
    {
        var coordinator = new DeviceCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connection = new CancellationTokenSource();
        try
        {
            var blocker = coordinator.EnqueueAndWaitAsync(async ct =>
            {
                started.SetResult();
                await release.Task.WaitAsync(ct);
                return CommandResult.Ok();
            }, Budget);
            await started.Task.WaitAsync(Budget);
            var first = coordinator.ToggleSwitchAsync(QuickSwitch.Efx, ActionSource.ManualPanel, Budget, connection.Token);
            var second = coordinator.ToggleSwitchAsync(QuickSwitch.Efx, ActionSource.ManualPanel, Budget, connection.Token);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(second.IsCompleted, "Repeated clicks must stay ordered instead of being dropped as busy.");
            connection.Cancel();
            Assert.AreEqual("cancelled", (await first.WaitAsync(Budget)).ErrorCode);
            Assert.AreEqual("cancelled", (await second.WaitAsync(Budget)).ErrorCode);
            // Both commands are already cancelled before the blocker is released: no device is opened.
            release.SetResult();
            await blocker.WaitAsync(Budget);
            await coordinator.EnqueueAndWaitAsync(_ => Task.FromResult(CommandResult.Ok()), Budget).WaitAsync(Budget);
            Assert.IsNull(coordinator.Session);
        }
        finally { release.TrySetResult(); coordinator.Shutdown(); }
    }

    [TestMethod]
    public async Task Disconnect_PreservesDetachedConfirmedStateUntilReconnectCompletes()
    {
        var coordinator = new DeviceCoordinator();
        // The device remains unopened throughout this test; only its session cache is populated.
        using var session = new DeviceSession(new MidiDevice());
        session.Global.Efx = 1;
        session.Global.Phantom48V = 1;
        session.Preset.MonoSw = 1;
        session.Preset.VolPot[0] = -12;
        session.Preset.FxParaVal[3] = 7;
        typeof(DeviceCoordinator).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, session);
        typeof(DeviceCoordinator).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, ConnectionState.Connected);
        try
        {
            var before = coordinator.GetStateSnapshot();
            Assert.IsTrue(before.Connected);
            Assert.AreEqual(1, before.Efx);
            Assert.IsTrue((await coordinator.DisconnectAsync("test").WaitAsync(Budget)).Success);
            var retained = coordinator.GetSwitchStates();
            Assert.IsTrue(retained.HasData);
            Assert.AreEqual(1, retained.Efx);
            Assert.AreEqual(1, retained.Phantom48V);
            Assert.AreEqual(1, retained.MonoSw);
            var disconnected = coordinator.GetStateSnapshot();
            Assert.IsFalse(disconnected.Connected);
            Assert.AreEqual(-12f, disconnected.VolPot[0]);
            Assert.AreEqual(7f, disconnected.FxParaVal[3]);
            disconnected.VolPot[0] = 0;
            disconnected.FxParaVal[3] = 0;
            Assert.AreEqual(-12f, coordinator.GetStateSnapshot().VolPot[0]);
            Assert.AreEqual(7f, coordinator.GetStateSnapshot().FxParaVal[3]);
            Assert.IsNull(coordinator.Session);

            // A new session's partial handshake must not overwrite the retained authority.
            using var nextSession = new DeviceSession(new MidiDevice());
            typeof(DeviceCoordinator).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, nextSession);
            typeof(DeviceCoordinator).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, ConnectionState.Connecting);
            Assert.IsFalse(coordinator.GetStateSnapshot().Connected);
            Assert.AreEqual(-12f, coordinator.GetStateSnapshot().VolPot[0]);
            nextSession.Preset.VolPot[0] = -6;
            typeof(DeviceCoordinator).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, ConnectionState.Connected);
            Assert.IsTrue(coordinator.GetStateSnapshot().Connected);
            Assert.AreEqual(-6f, coordinator.GetStateSnapshot().VolPot[0]);
        }
        finally { coordinator.Shutdown(); }
    }
}

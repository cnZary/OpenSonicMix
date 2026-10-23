using System.Runtime.InteropServices;
using System.Threading.Channels;
using SonicCube.Protocol.Commands;
using SonicCube.Protocol.Frames;
using SonicCube.Shared.Midi;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class MidiLifecycleTests
{
    [TestMethod]
    public async Task Send_HoldsMemoryUntilDone_AndCancellingWaitDoesNotReleaseIt()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        using var cts = new CancellationTokenSource();
        byte[] frame = [0xF0, 1, 2, 0xF7];
        var send = device.SendSysExAsync(frame, cts.Token);
        IntPtr header = await api.NextOutputAsync();
        cts.Cancel();
        await ExpectCancellation(send);
        frame[1] = 99;
        CollectionAssert.AreEqual(new byte[] { 0xF0, 1, 2, 0xF7 }, api.ReadOutput(header));
        Assert.AreEqual(0, api.OutputReleaseCount);
        api.CompleteOutput(header);
        await api.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, api.OutputReleaseCount);
        Assert.AreEqual(0, api.CallsInsideCallback);
    }

    [TestMethod]
    public async Task Send_SynchronousCompletionCallback_DoesNotDeadlockOrReleaseTwice()
    {
        var api = new FakeWinMm { CompleteDuringSend = true };
        using var device = Open(api);
        await device.SendSysExAsync([0xF0, 0xF7]).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, api.OutputReleaseCount);
        device.Close();
        Assert.AreEqual(1, api.OutputReleaseCount);
        Assert.AreEqual(0, api.CallsInsideCallback);
    }

    [TestMethod]
    public async Task Close_DuringSend_ResetsThenReleasesAndCompletesWaiters()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        var send = device.SendSysExAsync([0xF0, 0xF7]);
        await api.NextOutputAsync();
        await Task.Run(device.Close).WaitAsync(TimeSpan.FromSeconds(2));
        await ExpectCancellation(send);
        Assert.AreEqual(1, api.OutputReleaseCount);
        Assert.AreEqual(4, api.InputReleaseCount);
        Assert.IsTrue(api.OutputClosed);
        Assert.AreEqual(0, api.CallsInsideCallback);
    }

    [TestMethod]
    public async Task Close_StillPlaying_KeepsHeaderUntilLateCompletion()
    {
        var api = new FakeWinMm { CompleteOnReset = false };
        using var device = Open(api);
        var send = device.SendSysExAsync([0xF0, 7, 0xF7]);
        var header = await api.NextOutputAsync();
        device.Close();
        await ExpectCancellation(send);
        Assert.AreEqual(0, api.OutputReleaseCount);
        CollectionAssert.AreEqual(new byte[] { 0xF0, 7, 0xF7 }, api.ReadOutput(header));
        Assert.IsFalse(api.OutputClosed);
        api.CompleteOutput(header);
        await api.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await api.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, api.OutputReleaseCount);
        Assert.IsTrue(api.OutputClosed);
    }

    [TestMethod]
    public async Task Receive_ReturnsBufferBeforeSubscribers_AndKeepsOrderOffNativeCallback()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        var received = Channel.CreateUnbounded<byte>();
        device.DataReceived += bytes =>
        {
            Assert.IsFalse(FakeWinMm.InNativeCallback);
            Assert.AreEqual(4, api.AvailableInputCount);
            received.Writer.TryWrite(bytes[0]);
        };
        for (byte i = 1; i <= 20; i++)
        {
            api.Deliver([i]);
            Assert.AreEqual(i, await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        Assert.AreEqual(0, api.CallsInsideCallback);
    }

    [TestMethod]
    public async Task Receive_SubscriberException_DoesNotStopLaterEvents()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var success = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.ReceiveError += _ => error.TrySetResult();
        device.DataReceived += b => { if (b[0] == 1) throw new InvalidOperationException(); success.TrySetResult(); };
        api.Deliver([1]);
        await error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        api.Deliver([2]);
        await success.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task Receive_OldGenerationEvent_DoesNotRequeueNewConnectionBuffer()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        var oldCallback = api.CaptureInputCallback();
        device.Close();
        device.Open(0, 0, "new input", "new output");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        device.DataReceived += _ => done.TrySetResult();
        // A driver callback from the old port may arrive after the native allocator reused an address.
        oldCallback(api.PeekInput());
        api.Deliver([42]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(4, api.AvailableInputCount);
    }

    [TestMethod]
    public async Task Send_DoneBeforeUnprepareReady_RetriesWithoutPrematureFree()
    {
        var api = new FakeWinMm { CompleteDuringSend = true, UnprepareFailuresRemaining = 2 };
        using var device = Open(api);
        await device.SendSysExAsync([0xF0, 0xF7]).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, api.OutputReleaseCount);
        Assert.AreEqual(0, api.UnprepareFailuresRemaining);
    }

    [TestMethod]
    public async Task Send_UnprepareRetryExhausted_FailsWaiterButRetainsDriverMemory()
    {
        var api = new FakeWinMm { CompleteDuringSend = true, UnprepareFailuresRemaining = int.MaxValue };
        using var device = Open(api);
        var send = device.SendSysExAsync([0xF0, 9, 0xF7]);
        var header = await api.NextOutputAsync();
        try { await send.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Fail("Expected a driver failure"); }
        catch (MidiException) { }
        CollectionAssert.AreEqual(new byte[] { 0xF0, 9, 0xF7 }, api.ReadOutput(header));
        Assert.AreEqual(0, api.OutputReleaseCount);
        api.UnprepareFailuresRemaining = 0;
        device.Close();
        Assert.AreEqual(1, api.OutputReleaseCount);
    }

    [TestMethod]
    public async Task Send_RejectedButUnprepareTemporarilyFails_RetriesCleanup()
    {
        var api = new FakeWinMm { SendError = 5, UnprepareFailuresRemaining = 1 };
        using var device = Open(api);
        try { await device.SendSysExAsync([0xF0, 0xF7]); Assert.Fail("Expected a driver failure"); }
        catch (MidiException) { }
        await api.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(1, api.OutputReleaseCount);
    }

    [TestMethod]
    public async Task Close_ReopenWhileOldCleanupTimerPending_RetriesNewGeneration()
    {
        var api = new FakeWinMm { NotifyOnReset = false, UnprepareFailuresRemaining = 1 };
        using var device = Open(api);
        var first = device.SendSysExAsync([0xF0, 0xF7]);
        var firstHeader = await api.NextOutputAsync();
        device.Close();
        await ExpectCancellation(first);
        // Complete cleanup before the first generation's delayed retry wakes up.
        api.CompleteOutput(firstHeader);
        await api.Closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        device.Open(0, 0, "new input", "new output");
        var second = device.SendSysExAsync([0xF0, 0xF7]);
        await api.NextOutputAsync();
        api.UnprepareFailuresRemaining = 1;
        device.Close();
        await ExpectCancellation(second);
        await api.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await api.Released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(2, api.OutputReleaseCount);
    }

    [TestMethod]
    public async Task Session_SnapshotIsDetached_AndBroadcastRunsOutsideStateLockBeforeCompletion()
    {
        var api = new FakeWinMm { CompleteDuringSend = true };
        using var device = Open(api);
        using var session = new DeviceSession(device);
        var address = CommandAddress.WithLocation(CommandAddress.VolumePot, 0);
        Task<byte[]>? request = null;
        var snapshotSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.MessageReceived += (_, _) =>
        {
            try
            {
                // Waiting on another thread exposes an accidental callback-under-state-lock deadlock.
                var snapshot = Task.Run(() => session.GetStateSnapshot(true)).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                Assert.IsFalse(request!.IsCompleted);
                Assert.AreEqual(-12f, snapshot.VolPot[0]);
                snapshot.VolPot[0] = -50;
                Assert.AreEqual(-12f, session.GetStateSnapshot(true).VolPot[0]);
                session.SetModuleEnabled(0, true);
                Assert.AreEqual(1, session.GetStateSnapshot(true).FxModuleEn);
                snapshotSeen.TrySetResult();
            }
            catch (Exception ex) { snapshotSeen.TrySetException(ex); }
        };
        request = session.RequestAsync(address);
        await api.NextOutputAsync();
        api.Deliver(SysExFrame.Encode(address, FrameFlag.Request, Payload.Float32(-12))[0]);
        await request.WaitAsync(TimeSpan.FromSeconds(3));
        await snapshotSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task Session_CloseOnFinalHandshakeReply_DoesNotRestoreConnectedState()
    {
        var api = new FakeWinMm { CompleteDuringSend = true };
        using var device = Open(api);
        using var session = new DeviceSession(device);
        session.MessageReceived += (address, _) => { if (address == CommandAddress.Global) device.Close(); };
        var handshake = session.HandshakeAsync();
        foreach (var address in new[] { CommandAddress.SoftwareState, CommandAddress.Preset, CommandAddress.Global })
        {
            await api.NextOutputAsync();
            api.Deliver(SysExFrame.Encode(address, FrameFlag.Request, [])[0]);
        }
        await ExpectCancellation(handshake);
        Assert.IsFalse(session.IsConnected);
    }

    [TestMethod]
    public async Task Session_ConcurrentRequestsForSameAddress_DoNotOverwriteWaiters()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        using var session = new DeviceSession(device) { RequestRetries = 1 };
        var first = session.RequestAsync(CommandAddress.SoftwareState);
        var second = session.RequestAsync(CommandAddress.SoftwareState);
        var header = await api.NextOutputAsync();
        Assert.IsFalse(api.Sent.Reader.TryRead(out _));
        api.CompleteOutput(header);
        api.Deliver(SysExFrame.Encode(CommandAddress.SoftwareState, FrameFlag.Request, new byte[] { 1 })[0]);
        CollectionAssert.AreEqual(new byte[] { 1 }, await first.WaitAsync(TimeSpan.FromSeconds(2)));
        header = await api.NextOutputAsync();
        api.CompleteOutput(header);
        api.Deliver(SysExFrame.Encode(CommandAddress.SoftwareState, FrameFlag.Request, new byte[] { 2 })[0]);
        CollectionAssert.AreEqual(new byte[] { 2 }, await second.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [TestMethod]
    public async Task Session_Disconnect_CancelsResponseAndQueuedRequestImmediately()
    {
        var api = new FakeWinMm();
        using var device = Open(api);
        using var session = new DeviceSession(device) { ResponseTimeout = TimeSpan.FromSeconds(30) };
        var first = session.RequestAsync(CommandAddress.SoftwareState);
        var second = session.RequestAsync(CommandAddress.SoftwareState);
        api.CompleteOutput(await api.NextOutputAsync());
        device.Close();
        await ExpectCancellation(first);
        await ExpectCancellation(second);
        Assert.IsFalse(api.Sent.Reader.TryRead(out _));
    }

    [TestMethod]
    public async Task Session_TimeoutCleanup_AllowsNextRequestToComplete()
    {
        var api = new FakeWinMm { CompleteDuringSend = true };
        using var device = Open(api);
        using var session = new DeviceSession(device) { ResponseTimeout = TimeSpan.FromMilliseconds(50), RequestRetries = 1 };
        await ExpectCancellation(session.RequestAsync(CommandAddress.SoftwareState));
        await api.NextOutputAsync();
        session.ResponseTimeout = TimeSpan.FromSeconds(2);
        var next = session.RequestAsync(CommandAddress.SoftwareState);
        await api.NextOutputAsync();
        api.Deliver(SysExFrame.Encode(CommandAddress.SoftwareState, FrameFlag.Request, new byte[] { 3 })[0]);
        CollectionAssert.AreEqual(new byte[] { 3 }, await next.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    private static MidiDevice Open(FakeWinMm api)
    {
        var device = new MidiDevice(api);
        device.Open(0, 0, "fake input", "fake output");
        return device;
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.Fail("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }

    private sealed class FakeWinMm : IWinMmApi
    {
        [ThreadStatic] private static bool _inCallback;
        public static bool InNativeCallback => _inCallback;
        private readonly object _sync = new();
        private readonly Queue<IntPtr> _inputs = new();
        private readonly HashSet<IntPtr> _preparedInputs = new();
        private readonly Dictionary<IntPtr, bool> _outputs = new();
        private WinMM.MidiInProc? _input;
        private WinMM.MidiOutProc? _output;
        private IntPtr _instance;
        public readonly Channel<IntPtr> Sent = Channel.CreateUnbounded<IntPtr>();
        public readonly Channel<IntPtr> Released = Channel.CreateUnbounded<IntPtr>();
        public readonly TaskCompletionSource Closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompleteDuringSend { get; init; }
        public bool CompleteOnReset { get; init; } = true;
        public bool NotifyOnReset { get; init; } = true;
        public int SendError { get; init; }
        public int UnprepareFailuresRemaining;
        public int OutputReleaseCount;
        public int InputReleaseCount;
        public int CallsInsideCallback;
        public bool OutputClosed;
        public int AvailableInputCount { get { lock (_sync) return _inputs.Count; } }
        public Task<IntPtr> NextOutputAsync() => Sent.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        private void CheckCallback() { if (_inCallback) Interlocked.Increment(ref CallsInsideCallback); }
        public int OpenInput(out IntPtr handle, uint index, WinMM.MidiInProc callback, IntPtr instance)
        { CheckCallback(); handle = (IntPtr)1; _input = callback; _instance = instance; return 0; }
        public int OpenOutput(out IntPtr handle, uint index, WinMM.MidiOutProc callback, IntPtr instance)
        { CheckCallback(); handle = (IntPtr)2; _output = callback; return 0; }
        public int PrepareInput(IntPtr handle, IntPtr header, uint size)
        { CheckCallback(); lock (_sync) _preparedInputs.Add(header); return 0; }
        public int UnprepareInput(IntPtr handle, IntPtr header, uint size)
        { CheckCallback(); lock (_sync) { if (_inputs.Contains(header)) return WinMM.MIDIERR_STILLPLAYING; if (_preparedInputs.Remove(header)) InputReleaseCount++; } return 0; }
        public int AddInputBuffer(IntPtr handle, IntPtr header, uint size)
        { CheckCallback(); lock (_sync) _inputs.Enqueue(header); return 0; }
        public int StartInput(IntPtr handle) { CheckCallback(); return 0; }
        public int StopInput(IntPtr handle) { CheckCallback(); return 0; }
        public int ResetInput(IntPtr handle)
        {
            CheckCallback();
            lock (_sync)
                while (_inputs.TryDequeue(out var header))
                {
                    var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(header);
                    hdr.dwBytesRecorded = 0;
                    Marshal.StructureToPtr(hdr, header, false);
                    if (NotifyOnReset) Callback(() => _input!(handle, WinMM.MM_MIM_LONGDATA, _instance, header, IntPtr.Zero));
                }
            return 0;
        }
        public int CloseInput(IntPtr handle) { CheckCallback(); return 0; }
        public int PrepareOutput(IntPtr handle, IntPtr header, uint size)
        { CheckCallback(); lock (_sync) _outputs.Add(header, false); return 0; }
        public int UnprepareOutput(IntPtr handle, IntPtr header, uint size)
        {
            CheckCallback();
            lock (_sync)
            {
                if (_outputs.TryGetValue(header, out bool done) && !done) return WinMM.MIDIERR_STILLPLAYING;
                if (UnprepareFailuresRemaining > 0) { UnprepareFailuresRemaining--; return WinMM.MIDIERR_STILLPLAYING; }
                if (_outputs.Remove(header)) { OutputReleaseCount++; Released.Writer.TryWrite(header); }
            }
            return 0;
        }
        public int SendOutput(IntPtr handle, IntPtr header, uint size)
        {
            CheckCallback(); Sent.Writer.TryWrite(header);
            if (SendError != 0) { lock (_sync) _outputs[header] = true; return SendError; }
            if (CompleteDuringSend) CompleteOutput(header);
            return 0;
        }
        public int ResetOutput(IntPtr handle)
        {
            CheckCallback();
            lock (_sync) if (CompleteOnReset) foreach (var header in _outputs.Keys.ToArray())
            {
                if (NotifyOnReset) CompleteOutput(header);
                else _outputs[header] = true;
            }
            return 0;
        }
        public int CloseOutput(IntPtr handle)
        {
            CheckCallback(); lock (_sync)
            {
                if (_outputs.Count != 0) return WinMM.MIDIERR_STILLPLAYING;
                OutputClosed = true; Closed.TrySetResult(); return 0;
            }
        }
        public void CompleteOutput(IntPtr header)
        {
            lock (_sync)
            {
                Assert.IsTrue(_outputs.ContainsKey(header), "Driver lost its live output header");
                _outputs[header] = true;
                Callback(() => _output!((IntPtr)2, WinMM.MM_MOM_DONE, _instance, header, IntPtr.Zero));
            }
        }
        public byte[] ReadOutput(IntPtr header)
        {
            lock (_sync)
            {
                Assert.IsTrue(_outputs.ContainsKey(header));
                var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(header);
                var bytes = new byte[hdr.dwBufferLength];
                Marshal.Copy(hdr.lpData, bytes, 0, bytes.Length); return bytes;
            }
        }
        public void Deliver(byte[] data)
        {
            lock (_sync)
            {
                Assert.IsTrue(_inputs.TryDequeue(out var header));
                var hdr = Marshal.PtrToStructure<WinMM.MIDIHDR>(header);
                Marshal.Copy(data, 0, hdr.lpData, data.Length);
                hdr.dwBytesRecorded = (uint)data.Length;
                Marshal.StructureToPtr(hdr, header, false);
                Callback(() => _input!((IntPtr)1, WinMM.MM_MIM_LONGDATA, _instance, header, IntPtr.Zero));
            }
        }
        public IntPtr PeekInput() { lock (_sync) return _inputs.Peek(); }
        public Action<IntPtr> CaptureInputCallback()
        {
            var instance = _instance;
            var callback = _input!;
            return header => Callback(() => callback((IntPtr)1, WinMM.MM_MIM_LONGDATA, instance, header, IntPtr.Zero));
        }
        private static void Callback(Action action)
        { _inCallback = true; try { action(); } finally { _inCallback = false; } }
    }
}

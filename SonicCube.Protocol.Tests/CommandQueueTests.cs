using SonicCube.Shared.Ipc;

namespace SonicCube.Protocol.Tests;

[TestClass]
public sealed class CommandQueueTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static SerialCommandQueue<string, string> CreateQueue() => new((code, _) => code);

    [TestMethod]
    [DataRow(100)]
    [DataRow(200)]
    [DataRow(300)]
    public async Task SlowDevice_DragKeepsInFlightAndLatestOnly(int deviceLatencyMs)
    {
        var queue = CreateQueue();
        var started = Gate();
        var release = Gate();
        var writes = new List<int>();
        int active = 0, maximumActive = 0;
        async Task<string> Write(int value, CancellationToken ct)
        {
            maximumActive = Math.Max(maximumActive, ++active);
            try
            {
                writes.Add(value);
                if (value == 0) { started.SetResult(); await release.Task.WaitAsync(ct); }
                await Task.Delay(deviceLatencyMs, ct); // Device write + readback latency.
                return "ok";
            }
            finally { active--; }
        }
        try
        {
            var first = queue.EnqueueLatest("volume:0", ct => Write(0, ct), Budget);
            await started.Task.WaitAsync(Budget);
            var requests = Enumerable.Range(1, 100)
                .Select(value => queue.EnqueueLatest("volume:0", ct => Write(value, ct), Budget)).ToArray();
            Assert.AreEqual(1, queue.PendingCount);
            foreach (var superseded in requests[..^1]) Assert.AreEqual("superseded", await superseded.WaitAsync(Budget));
            release.SetResult();
            Assert.AreEqual("ok", await first.WaitAsync(Budget));
            Assert.AreEqual("ok", await requests[^1].WaitAsync(Budget));
            CollectionAssert.AreEqual(new[] { 0, 100 }, writes);
            Assert.AreEqual(1, maximumActive);
        }
        finally { queue.Stop(); await queue.Completion.WaitAsync(Budget); }
    }

    [TestMethod]
    public async Task DistinctFullAddressesAndFxKeys_RetainTheirOwnLatestValues()
    {
        var queue = new SerialCommandQueue<(uint Address, int Module, int Parameter), string>((code, _) => code);
        var started = Gate();
        var release = Gate();
        var writes = new List<string>();
        try
        {
            var blocker = queue.Enqueue(async ct => { started.SetResult(); await release.Task.WaitAsync(ct); return "ok"; }, Budget);
            await started.Task.WaitAsync(Budget);
            var old = queue.EnqueueLatest((0x00010010, -1, -1), _ => { writes.Add("old"); return Task.FromResult("ok"); }, Budget);
            var a = queue.EnqueueLatest((0x00010010, -1, -1), _ => { writes.Add("ch0"); return Task.FromResult("ok"); }, Budget);
            var b = queue.EnqueueLatest((0x00010110, -1, -1), _ => { writes.Add("ch1"); return Task.FromResult("ok"); }, Budget);
            var c = queue.EnqueueLatest((0, 1, 3), _ => { writes.Add("fx1:3"); return Task.FromResult("ok"); }, Budget);
            var d = queue.EnqueueLatest((0, 2, 3), _ => { writes.Add("fx2:3"); return Task.FromResult("ok"); }, Budget);
            var e = queue.EnqueueLatest((0, 2, 4), _ => { writes.Add("fx2:4"); return Task.FromResult("ok"); }, Budget);
            Assert.AreEqual(5, queue.PendingCount);
            Assert.AreEqual("superseded", await old.WaitAsync(Budget));
            release.SetResult();
            await Task.WhenAll(blocker, a, b, c, d, e).WaitAsync(Budget);
            CollectionAssert.AreEqual(new[] { "ch0", "ch1", "fx1:3", "fx2:3", "fx2:4" }, writes);
        }
        finally { queue.Stop(); await queue.Completion.WaitAsync(Budget); }
    }

    [TestMethod]
    public async Task ResetAndAlgorithm_AreOrderingBarriersForContinuousWrites()
    {
        var queue = CreateQueue();
        var started = Gate();
        var release = Gate();
        var writes = new List<string>();
        Task<string> Write(string value, CancellationToken _) { writes.Add(value); return Task.FromResult("ok"); }
        try
        {
            var blocker = queue.Enqueue(async ct => { started.SetResult(); await release.Task.WaitAsync(ct); return "ok"; }, Budget);
            await started.Task.WaitAsync(Budget);
            var old = queue.EnqueueLatest("fx:0:1", ct => Write("old", ct), Budget);
            var before = queue.EnqueueLatest("fx:0:1", ct => Write("before-reset", ct), Budget);
            var reset = queue.Enqueue(ct => Write("reset", ct), Budget);
            var after = queue.EnqueueLatest("fx:0:1", ct => Write("after-reset", ct), Budget);
            var algorithm = queue.Enqueue(ct => Write("algorithm", ct), Budget);
            var defaults = queue.EnqueueLatest("fx:0:1", ct => Write("default", ct), Budget);
            var final = queue.EnqueueLatest("fx:0:1", ct => Write("final", ct), Budget);
            Assert.AreEqual("superseded", await old.WaitAsync(Budget));
            Assert.AreEqual("superseded", await defaults.WaitAsync(Budget));
            release.SetResult();
            await Task.WhenAll(blocker, before, reset, after, algorithm, final).WaitAsync(Budget);
            CollectionAssert.AreEqual(new[] { "before-reset", "reset", "after-reset", "algorithm", "final" }, writes);
        }
        finally { queue.Stop(); await queue.Completion.WaitAsync(Budget); }
    }

    [TestMethod]
    public async Task PendingExpiryAndConnectionCancellation_NeverExecuteLater()
    {
        var queue = CreateQueue();
        var started = Gate();
        var release = Gate();
        using var connection = new CancellationTokenSource();
        int writes = 0;
        try
        {
            var blocker = queue.Enqueue(async ct => { started.SetResult(); await release.Task.WaitAsync(ct); return "ok"; }, Budget);
            await started.Task.WaitAsync(Budget);
            var expired = queue.EnqueueLatest("a", _ => { writes++; return Task.FromResult("ok"); }, TimeSpan.FromMilliseconds(40));
            var cancelled = queue.EnqueueLatest("b", _ => { writes++; return Task.FromResult("ok"); }, Budget, connection.Token);
            connection.Cancel();
            Assert.AreEqual("timeout", await expired.WaitAsync(Budget));
            Assert.AreEqual("cancelled", await cancelled.WaitAsync(Budget));
            Assert.AreEqual("timeout", await queue.Enqueue(_ => { writes++; return Task.FromResult("ok"); }, TimeSpan.Zero));
            release.SetResult();
            await blocker.WaitAsync(Budget);
            await queue.Enqueue(_ => Task.FromResult("drained"), Budget).WaitAsync(Budget);
            Assert.AreEqual(0, writes);
        }
        finally { queue.Stop(); await queue.Completion.WaitAsync(Budget); }
    }

    [TestMethod]
    public async Task InFlightTimeout_CancelsBeforeNextCommandRuns()
    {
        var queue = CreateQueue();
        var started = Gate();
        bool active = false, laterWrite = false;
        try
        {
            var expired = queue.Enqueue(async ct =>
            {
                active = true;
                started.SetResult();
                try { await Task.Delay(300, ct); laterWrite = true; return "ok"; }
                finally { active = false; }
            }, TimeSpan.FromMilliseconds(100));
            await started.Task.WaitAsync(Budget);
            var next = queue.Enqueue(_ => Task.FromResult(active ? "overlap" : "ok"), Budget);
            Assert.AreEqual("timeout", await expired.WaitAsync(Budget));
            Assert.AreEqual("ok", await next.WaitAsync(Budget));
            Assert.IsFalse(laterWrite);
        }
        finally { queue.Stop(); await queue.Completion.WaitAsync(Budget); }
    }

    [TestMethod]
    public async Task Shutdown_CompletesQueuedAndInFlightRequestsAndRejectsNewWork()
    {
        var queue = CreateQueue();
        var started = Gate();
        int writes = 0;
        var running = queue.Enqueue(async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "ok"; }, Budget);
        await started.Task.WaitAsync(Budget);
        var pending = queue.EnqueueLatest("a", _ => { writes++; return Task.FromResult("ok"); }, Budget);
        queue.Stop();
        Assert.AreEqual("shutdown", await running.WaitAsync(Budget));
        Assert.AreEqual("shutdown", await pending.WaitAsync(Budget));
        Assert.AreEqual("shutdown", await queue.Enqueue(_ => Task.FromResult("ok"), Budget));
        await queue.Completion.WaitAsync(Budget);
        Assert.AreEqual(0, queue.PendingCount);
        Assert.AreEqual(0, writes);
        Assert.IsFalse(queue.HasPendingWork);
    }
}

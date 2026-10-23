using System.Diagnostics;

namespace SonicCube.Shared.Ipc;

/// <summary>
/// One asynchronous worker. Continuous writes retain only the latest pending value per key;
/// every ordinary command is an ordering barrier. Time budgets include time spent waiting.
/// </summary>
public sealed class SerialCommandQueue<TKey, TResult> where TKey : notnull
{
    private readonly object _sync = new();
    private readonly LinkedList<Entry> _queue = new();
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _latest = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<string, Exception?, TResult> _failure;
    private readonly Task _worker;
    private bool _stopped, _busy;

    private sealed class Entry : IDisposable
    {
        public required Func<CancellationToken, Task<TResult>> Operation;
        public required CancellationTokenSource Cancellation;
        public required TimeSpan Timeout;
        public long StartedAt = Stopwatch.GetTimestamp();
        public TaskCompletionSource<TResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration;
        public TKey? Key;
        public bool IsContinuous;
        public void Dispose() { Registration.Dispose(); Cancellation.Dispose(); }
    }

    public SerialCommandQueue(Func<string, Exception?, TResult> failure)
    {
        _failure = failure;
        _worker = Task.Run(WorkAsync);
    }

    public bool HasPendingWork { get { lock (_sync) return _busy || _queue.Count > 0; } }
    public int PendingCount { get { lock (_sync) return _queue.Count; } }
    public Task Completion => _worker;
    public event Action? CommandCompleted;

    public Task<TResult> Enqueue(Func<CancellationToken, Task<TResult>> operation, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Add(operation, timeout, cancellationToken, false, default);

    public Task<TResult> EnqueueLatest(TKey key, Func<CancellationToken, Task<TResult>> operation, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Add(operation, timeout, cancellationToken, true, key);

    private Task<TResult> Add(Func<CancellationToken, Task<TResult>> operation, TimeSpan timeout, CancellationToken cancellationToken, bool continuous, TKey? key)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Entry? replaced = null;
        Entry entry;
        lock (_sync)
        {
            if (_stopped) return Task.FromResult(_failure("shutdown", null));
            // Even an already expired discrete request divides the stream into separate segments.
            if (!continuous) _latest.Clear();
            if (timeout <= TimeSpan.Zero) return Task.FromResult(_failure("timeout", null));
            if (cancellationToken.IsCancellationRequested) return Task.FromResult(_failure("cancelled", null));
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
            entry = new Entry { Operation = operation, Cancellation = cancellation, Timeout = timeout, Key = key, IsContinuous = continuous };
            entry.Registration = cancellation.Token.Register(() => entry.Completion.TrySetResult(
                _failure(_shutdown.IsCancellationRequested ? "shutdown" : cancellationToken.IsCancellationRequested ? "cancelled" : "timeout", null)));
            cancellation.CancelAfter(timeout);
            if (continuous && _latest.TryGetValue(key!, out var previous))
            {
                replaced = previous.Value;
                // Retain the key's position so other keys cannot starve it during a long drag.
                previous.Value = entry;
            }
            else
            {
                var node = _queue.AddLast(entry);
                if (continuous) _latest[key!] = node;
            }
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        if (replaced is not null)
        {
            replaced.Completion.TrySetResult(_failure("superseded", null));
            replaced.Dispose();
        }
        return entry.Completion.Task;
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            Entry? entry;
            lock (_sync)
            {
                if (_stopped) return;
                var node = _queue.First;
                entry = node?.Value;
                if (node is not null)
                {
                    _queue.RemoveFirst();
                    if (entry!.IsContinuous && _latest.TryGetValue(entry.Key!, out var current) && ReferenceEquals(current, node))
                        _latest.Remove(entry.Key!);
                    _busy = true;
                }
            }
            if (entry is null)
            {
                await _signal.WaitAsync().ConfigureAwait(false);
                continue;
            }
            try
            {
                if (entry.Completion.Task.IsCompleted) continue;
                if (Stopwatch.GetElapsedTime(entry.StartedAt) >= entry.Timeout)
                {
                    entry.Completion.TrySetResult(_failure("timeout", null));
                    continue;
                }
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                var result = await entry.Operation(entry.Cancellation.Token).ConfigureAwait(false);
                entry.Cancellation.Token.ThrowIfCancellationRequested();
                entry.Completion.TrySetResult(Stopwatch.GetElapsedTime(entry.StartedAt) >= entry.Timeout ? _failure("timeout", null) : result);
            }
            catch (OperationCanceledException)
            {
                entry.Completion.TrySetResult(_failure(_shutdown.IsCancellationRequested ? "shutdown" : "timeout", null));
            }
            catch (Exception ex) { entry.Completion.TrySetResult(_failure("error", ex)); }
            finally
            {
                entry.Dispose();
                lock (_sync) _busy = false;
                try { CommandCompleted?.Invoke(); } catch { }
            }
        }
    }

    /// <summary>Reject new work, cancel the current operation and complete all queued callers.</summary>
    public void Stop()
    {
        Entry[] pending;
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            pending = _queue.ToArray();
            _queue.Clear();
            _latest.Clear();
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        _shutdown.Cancel();
        foreach (var entry in pending)
        {
            entry.Completion.TrySetResult(_failure("shutdown", null));
            entry.Dispose();
        }
    }
}

using System.IO.Pipes;
using System.Text.Json;

namespace SonicCube.Shared.Ipc;

/// <summary>
/// JSON Lines 命名管道消息帧读写(每行一个完整 JSON 对象,UTF-8)。
/// 限制单条消息长度;异步读写;写入串行化。
/// </summary>
public static class IpcFraming
{
    public const int MaxMessageBytes = 64 * 1024;

    private static readonly byte[] Newline = "\n"u8.ToArray();

    /// <summary>异步写一行 JSON(调用方需自行串行化写入)。</summary>
    public static async Task WriteAsync(PipeStream stream, IpcMessage msg, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(msg, IpcJsonContext.Default.IpcMessage);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.WriteAsync(Newline, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>从缓冲解析完整行,返回消息与剩余缓冲。buffer 内容原地前移。</summary>
    public static bool TryReadLine(byte[] buffer, ref int length, out IpcMessage? msg, out string? error)
    {
        msg = null;
        error = null;
        int nl = Array.IndexOf(buffer, (byte)'\n', 0, length);
        if (nl < 0)
        {
            if (length > MaxMessageBytes)
            {
                error = "消息超过最大长度限制";
                length = 0;
            }
            return false;
        }
        int lineLength = nl;
        while (lineLength > 0 && buffer[lineLength - 1] == '\r') lineLength--;
        try
        {
            // 直接读取 UTF-8,避免高频电平消息先分配 UTF-16 字符串再转码。
            if (lineLength > 0)
                msg = JsonSerializer.Deserialize(buffer.AsSpan(0, lineLength), IpcJsonContext.Default.IpcMessage);
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败: {ex.Message}";
        }
        finally
        {
            // 解析仍引用原缓冲,必须在解析完成后才移动下一行。
            Buffer.BlockCopy(buffer, nl + 1, buffer, 0, length - nl - 1);
            length -= nl + 1;
        }
        return msg != null;
    }
}

/// <summary>
/// 通用管道连接封装:异步读循环 + 队列化写泵(保序) + 消息分发。
/// Tray(Server)与 Panel(Client)共用。
/// </summary>
public sealed class PipeConnection : IDisposable
{
    private readonly PipeStream _stream;
    // A null message is the single pending-meter marker. Reliable messages keep their
    // FIFO positions; replacing a meter never adds another item behind a blocked pipe.
    private readonly System.Threading.Channels.Channel<(IpcMessage? Msg, TaskCompletionSource? Tcs, CancellationToken Token)> _writeQueue =
        System.Threading.Channels.Channel.CreateUnbounded<(IpcMessage?, TaskCompletionSource?, CancellationToken)>();
    private readonly object _meterSync = new();
    private IpcMessage? _pendingMeter;
    private bool _meterQueued;
    private readonly byte[] _buf = new byte[IpcFraming.MaxMessageBytes + 1];
    private int _len;
    private int _stopped;
    private CancellationTokenSource? _readCts;
    private CancellationTokenSource? _writeCts;

    public event Action<IpcMessage>? MessageReceived;
    public event Action? Disconnected;

    public PipeStream Stream => _stream;
    public bool IsConnected => Volatile.Read(ref _stopped) == 0 && _stream.IsConnected;

    public PipeConnection(PipeStream stream) => _stream = stream;

    /// <summary>入队发送(单写泵串行化,保证消息按入队顺序写出)。</summary>
    public Task SendAsync(IpcMessage msg, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        if (!_writeQueue.Writer.TryWrite((msg, tcs, ct)))
        {
            tcs.TrySetException(new InvalidOperationException("写泵已停止"));
            return tcs.Task;
        }
        return ct.CanBeCanceled ? tcs.Task.WaitAsync(ct) : tcs.Task;
    }

    /// <summary>尽力入队。电平仅保留最新待发值,其他广播与请求保持 FIFO。</summary>
    public void Post(IpcMessage msg)
    {
        if (msg.Type != IpcMessageType.MeterUpdate)
        {
            _writeQueue.Writer.TryWrite((msg, null, default));
            return;
        }

        lock (_meterSync)
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _pendingMeter = msg;
            if (_meterQueued) return;
            _meterQueued = true;
            if (!_writeQueue.Writer.TryWrite((null, null, default)))
            {
                _pendingMeter = null;
                _meterQueued = false;
            }
        }
    }

    public void StartReadLoop()
    {
        _readCts = new CancellationTokenSource();
        _writeCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
        _ = Task.Run(() => WriteLoopAsync(_writeCts.Token));
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var (queuedMessage, tcs, token) in _writeQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var msg = queuedMessage;
                if (msg == null)
                {
                    lock (_meterSync)
                    {
                        msg = _pendingMeter;
                        _pendingMeter = null;
                        _meterQueued = false;
                    }
                    if (msg == null) continue;
                }
                if (token.IsCancellationRequested) { tcs?.TrySetCanceled(token); continue; }
                try
                {
                    await IpcFraming.WriteAsync(_stream, msg, ct).ConfigureAwait(false);
                    tcs?.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs?.TrySetException(ex);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Dispose();
            while (_writeQueue.Reader.TryRead(out var pending))
                pending.Tcs?.TrySetException(new IOException("Pipe disconnected"));
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _stream.IsConnected)
            {
                int n = await _stream.ReadAsync(_buf.AsMemory(_len, _buf.Length - _len), ct).ConfigureAwait(false);
                if (n <= 0) break;
                _len += n;
                while (true)
                {
                    if (_len >= _buf.Length) { _len = 0; break; } // 溢出,丢弃
                    if (!IpcFraming.TryReadLine(_buf, ref _len, out var msg, out var err))
                    {
                        if (err != null) return;
                        break;
                    }
                    if (msg != null) MessageReceived?.Invoke(msg);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception) { }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        lock (_meterSync)
        {
            _pendingMeter = null;
            _meterQueued = false;
        }
        _readCts?.Cancel();
        _writeCts?.Cancel();
        _writeQueue.Writer.TryComplete();
        try { _stream.Dispose(); } catch { }
        Disconnected?.Invoke();
    }
}

using System.IO.Pipes;
using System.Text;
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
        var line = Encoding.UTF8.GetString(buffer, 0, nl).TrimEnd('\r');
        // 移除已消费行
        Buffer.BlockCopy(buffer, nl + 1, buffer, 0, length - nl - 1);
        length -= nl + 1;
        if (line.Length == 0) return false;
        try
        {
            msg = JsonSerializer.Deserialize(line, IpcJsonContext.Default.IpcMessage);
        }
        catch (JsonException ex)
        {
            error = $"JSON 解析失败: {ex.Message}";
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
    private readonly System.Threading.Channels.Channel<(IpcMessage Msg, TaskCompletionSource? Tcs)> _writeQueue =
        System.Threading.Channels.Channel.CreateUnbounded<(IpcMessage, TaskCompletionSource?)>();
    private readonly byte[] _buf = new byte[IpcFraming.MaxMessageBytes + 1];
    private int _len;
    private CancellationTokenSource? _readCts;
    private CancellationTokenSource? _writeCts;

    public event Action<IpcMessage>? MessageReceived;
    public event Action? Disconnected;

    public PipeStream Stream => _stream;
    public bool IsConnected => _stream.IsConnected;

    public PipeConnection(PipeStream stream) => _stream = stream;

    /// <summary>入队发送(单写泵串行化,保证消息按入队顺序写出)。</summary>
    public Task SendAsync(IpcMessage msg, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite((msg, tcs)))
        {
            tcs.TrySetException(new InvalidOperationException("写泵已停止"));
            return tcs.Task;
        }
        if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    /// <summary>尽力入队,不等待完成(用于高频广播,调用方不关心结果)。</summary>
    public void Post(IpcMessage msg) => _writeQueue.Writer.TryWrite((msg, null));

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
            await foreach (var (msg, tcs) in _writeQueue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await IpcFraming.WriteAsync(_stream, msg, ct).ConfigureAwait(false);
                    tcs?.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs?.TrySetException(ex);
                    if (ex is IOException or OperationCanceledException) break; // 连接断开,停泵
                }
            }
        }
        catch (OperationCanceledException) { }
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
                        if (err != null) Disconnected?.Invoke();
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
            Disconnected?.Invoke();
        }
    }

    public void Dispose()
    {
        _readCts?.Cancel();
        _writeCts?.Cancel();
        _writeQueue.Writer.TryComplete();
        try { _stream.Dispose(); } catch { }
    }
}

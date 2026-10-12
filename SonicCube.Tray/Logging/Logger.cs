using System.Collections.Concurrent;
using System.Text;

namespace SonicCube.Tray.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// 异步滚动日志(见需求第十四节)。
/// 后台批量写盘,队列有上限;极端情况下丢弃时记录丢失计数与告警,不假称完整。
/// 高频电平按采样策略,不逐条刷盘。
/// </summary>
public static class Logger
{
    private sealed record Entry(DateTime Ts, LogLevel Level, string Category, string Message);

    private static readonly ConcurrentQueue<Entry> _queue = new();
    private static readonly AutoResetEvent _signal = new(false);
    private static Thread? _writer;
    private static volatile bool _running;

    private static string _dir = Settings.SettingsStore.LogsDir;
    private static long _maxFileSize = 10L * 1024 * 1024;
    private static int _maxFiles = 5;
    private static int _maxQueue = 4096;
    private static long _dropped;

    private static FileStream? _stream;
    private static string? _currentFile;
    private static long _currentSize;
    private static DateTime _lastMeterFlush = DateTime.MinValue;
    private static readonly object _fileSync = new();

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    /// <summary>电平类高频日志的最小刷盘间隔。</summary>
    public static TimeSpan MeterFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public static void Start(string dir, long maxFileSizeBytes, int maxFiles)
    {
        if (_running) return;
        _dir = dir;
        _maxFileSize = Math.Max(64 * 1024, maxFileSizeBytes);
        _maxFiles = Math.Max(1, maxFiles);
        Directory.CreateDirectory(_dir);
        _running = true;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "SonicCube.Logger" };
        _writer.Start();
    }

    public static void Stop()
    {
        _running = false;
        _signal.Set();
        _writer?.Join(2000);
        FlushAll();
        lock (_fileSync) { _stream?.Dispose(); _stream = null; }
    }

    public static void Debug(string cat, string msg) => Enqueue(LogLevel.Debug, cat, msg);
    public static void Info(string cat, string msg) => Enqueue(LogLevel.Info, cat, msg);
    public static void Warn(string cat, string msg) => Enqueue(LogLevel.Warn, cat, msg);
    public static void Error(string cat, string msg) => Enqueue(LogLevel.Error, cat, msg);

    /// <summary>记录 MIDI 帧(方向 + 原始字节)。</summary>
    public static void MidiFrame(bool tx, uint cmdAddr, ReadOnlySpan<byte> frame)
    {
        // level 早退必须在格式化之前,避免 MinLevel>Debug 时白构建 hex/插值字符串。
        if (LogLevel.Debug < MinLevel || !_running) return;
        Enqueue(LogLevel.Debug, "MIDI",
            $"{(tx ? "TX" : "RX")} addr=0x{cmdAddr:X8} {Convert.ToHexString(frame)}");
    }

    /// <summary>高频电平日志(按 MeterFlushInterval 采样)。</summary>
    public static void Meter(ReadOnlySpan<float> meters)
    {
        if (LogLevel.Debug < MinLevel || !_running) return;
        var now = DateTime.UtcNow;
        if (now - _lastMeterFlush < MeterFlushInterval) return;
        _lastMeterFlush = now;
        var sb = new StringBuilder(64);
        for (int i = 0; i < meters.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(meters[i].ToString("0.0"));
        }
        Enqueue(LogLevel.Debug, "Meter", sb.ToString());
    }

    private static void Enqueue(LogLevel level, string cat, string msg)
    {
        if (level < MinLevel || !_running) return;
        if (_queue.Count >= _maxQueue)
        {
            var dropped = Interlocked.Increment(ref _dropped);
            if (dropped == 1 || dropped % 1000 == 0)
                _queue.Enqueue(new Entry(DateTime.Now, LogLevel.Warn, "Logger",
                    $"日志队列已满,已丢弃 {dropped} 条(日志不完整)"));
            return;
        }
        _queue.Enqueue(new Entry(DateTime.Now, level, cat, msg));
        _signal.Set();
    }

    private static void WriterLoop()
    {
        var batch = new List<Entry>(64);
        while (_running)
        {
            _signal.WaitOne(500);
            Drain(batch);
            if (batch.Count > 0) WriteBatch(batch);
        }
        Drain(batch);
        if (batch.Count > 0) WriteBatch(batch);
    }

    private static void Drain(List<Entry> batch)
    {
        batch.Clear();
        while (batch.Count < 256 && _queue.TryDequeue(out var e)) batch.Add(e);
    }

    private static void FlushAll()
    {
        var batch = new List<Entry>();
        Drain(batch);
        int guard = 0;
        while (batch.Count > 0 && guard++ < 100)
        {
            WriteBatch(batch);
            Drain(batch);
        }
    }

    private static void WriteBatch(List<Entry> batch)
    {
        lock (_fileSync)
        {
            try
            {
                EnsureStream();
                if (_stream == null) return;
                foreach (var e in batch)
                {
                    var line = $"{e.Ts:yyyy-MM-dd HH:mm:ss.fff} [{e.Level,-5}] [{e.Category}] {e.Message}\r\n";
                    var bytes = Encoding.UTF8.GetBytes(line);
                    if (_currentSize + bytes.Length > _maxFileSize) Roll();
                    _stream.Write(bytes, 0, bytes.Length);
                    _currentSize += bytes.Length;
                }
                _stream.Flush();
            }
            catch
            {
                // 日志失败不应影响主流程。
            }
        }
    }

    private static void EnsureStream()
    {
        if (_stream != null) return;
        _currentFile = Path.Combine(_dir, "tray.log");
        var fi = new FileInfo(_currentFile);
        _currentSize = fi.Exists ? fi.Length : 0;
        if (_currentSize >= _maxFileSize) Roll();
        _stream = new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private static void Roll()
    {
        _stream?.Dispose();
        _stream = null;
        _currentSize = 0;
        // 移位:tray.4.log ← tray.3.log ... tray.log → tray.1.log
        for (int i = _maxFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"tray.{i}.log");
            var dst = Path.Combine(_dir, $"tray.{i + 1}.log");
            try { if (File.Exists(src)) File.Move(src, dst, overwrite: true); } catch { }
        }
        var first = Path.Combine(_dir, "tray.1.log");
        try { if (File.Exists(_currentFile)) File.Move(_currentFile!, first, overwrite: true); } catch { }
        _stream = new FileStream(_currentFile!, FileMode.Create, FileAccess.Write, FileShare.Read);
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 音量推子(dB 分段线性映射)。协议 dB(-51~+10)经 ValueMapping 分段映射到等距行程;
/// 拖动合并节流发送;双击复位 0;滚轮 ±1dB。
/// 视觉:左侧 dB 数字刻度(0 位高亮)、双缘刻度线、圆角轨道、三段式 thumb
/// (双色填充 + 高光 + 亮色抓线,悬停/拖动提亮)。
/// 数值文本由通道条在推子底部单独呈现(较低频率更新)。
/// </summary>
public sealed class VolumeFaderControl : FrameworkElement
{
    public VolumeFaderControl()
    {
        Width = 48;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public static readonly DependencyProperty DbProperty = DependencyProperty.Register(
        nameof(Db), typeof(double), typeof(VolumeFaderControl),
        new FrameworkPropertyMetadata(-51.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDbChanged));
    /// <summary>协议 dB 值。</summary>
    public double Db { get => (double)GetValue(DbProperty); set => SetValue(DbProperty, value); }

    /// <summary>节流后提交协议 dB。</summary>
    public event Action<double>? ValueCommitted;
    /// <summary>拖动中即时值(用于底部数值标签)。</summary>
    public event Action<double>? ValueChanging;

    public const double MinDb = -51;
    public const double MaxDb = 10;

    private bool _dragging;
    private double _pendingDb = double.NaN;
    private double _lastSentDb = double.NaN;
    private System.Windows.Threading.DispatcherTimer? _sendTimer;

    private static void OnDbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (VolumeFaderControl)d;
        double v = Math.Clamp((double)e.NewValue, MinDb, MaxDb);
        if (!Equals(e.NewValue, v)) { f.SetCurrentValue(DbProperty, v); return; }
        f.InvalidateVisual();
    }

    public void SetDbSilently(double db)
    {
        // 拖动中不回显。提交后若回显值仍明显偏离最后提交值,说明是旧快照,忽略。
        if (_dragging) return;
        if (!double.IsNaN(_awaitingConfirmDb))
        {
            if (Math.Abs(db - _awaitingConfirmDb) < 0.05) _awaitingConfirmDb = double.NaN; // 回包确认,恢复
            else return; // 旧快照,忽略
        }
        SetCurrentValue(DbProperty, Math.Clamp(db, MinDb, MaxDb));
    }

    private void SetDbInternal(double db) => SetCurrentValue(DbProperty, Math.Clamp(db, MinDb, MaxDb));
    private DateTime _suppressEchoUntil = DateTime.MinValue;
    private double _awaitingConfirmDb = double.NaN;
    private void SuppressEchoBriefly() => _suppressEchoUntil = DateTime.UtcNow.AddMilliseconds(600);

    private double TrackTop => 4;
    private double TrackHeight => Math.Max(10, ActualHeight - 8);

    private double DbToY(double db)
    {
        double pos = ValueMapping.FaderDbToPosition(db);
        double t = (pos - MinDb) / (MaxDb - MinDb);
        return TrackTop + (1 - t) * TrackHeight;
    }

    private double YToDb(double y)
    {
        double t = 1 - (y - TrackTop) / TrackHeight;
        double pos = MinDb + Math.Clamp(t, 0, 1) * (MaxDb - MinDb);
        return ValueMapping.FaderPositionToDb(pos);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            SetDbInternal(0);
            QueueSend(0);
            FlushPending();
            SuppressEchoBriefly();
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        UpdateFromY(e.GetPosition(this).Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) UpdateFromY(e.GetPosition(this).Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        FlushPending();
        SuppressEchoBriefly();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double nd = Math.Clamp(Db + (e.Delta > 0 ? 1 : -1), MinDb, MaxDb);
        SetDbInternal(nd);
        QueueSend(nd);
        FlushPending();
        SuppressEchoBriefly();
        e.Handled = true;
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double nd = e.Key switch
        {
            Key.Up => Db + 1,
            Key.Down => Db - 1,
            Key.PageUp => Db + 5,
            Key.PageDown => Db - 5,
            Key.Home => MaxDb,
            Key.End => MinDb,
            _ => Db,
        };
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            nd = Math.Clamp(nd, MinDb, MaxDb);
            SetDbInternal(nd);
            QueueSend(nd);
            FlushPending();
            SuppressEchoBriefly();
            e.Handled = true;
        }
    }

    private void UpdateFromY(double y)
    {
        double nd = Math.Clamp(YToDb(y), MinDb, MaxDb);
        if (Math.Abs(nd - Db) < 0.05) return;
        SetDbInternal(nd);
        ValueChanging?.Invoke(nd);
        QueueSend(nd);
    }

    private void QueueSend(double db)
    {
        _pendingDb = db;
        _sendTimer ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50), System.Windows.Threading.DispatcherPriority.Normal,
            (_, _) => FlushPending(), Dispatcher);
        if (!_sendTimer.IsEnabled) _sendTimer.Start();
    }

    private void FlushPending()
    {
        _sendTimer?.Stop();
        if (double.IsNaN(_pendingDb)) return;
        if (Math.Abs(_pendingDb - _lastSentDb) < 0.01) { _pendingDb = double.NaN; return; }
        _lastSentDb = _pendingDb;
        _pendingDb = double.NaN;
        _awaitingConfirmDb = _lastSentDb; // 等待该值回包确认后再恢复回显
        ValueCommitted?.Invoke(_lastSentDb);
    }

    // ── 画刷/文字资源缓存 ──
    private Brush? _tick, _text, _textHi, _track, _thumbBorder, _thumbLine, _accent, _highlight;
    private Pen? _tickPen, _thumbBorderPen, _thumbLinePen, _thumbLineAccentPen;
    private Typeface? _typeface;

    private void EnsureResources()
    {
        if (_thumbLine is not null) return;
        _tick = (Brush)FindResource("BrushTickMajor");
        _text = (Brush)FindResource("BrushTextDisabled");
        _textHi = (Brush)FindResource("BrushTextSecondary");
        _track = (Brush)FindResource("BrushFaderTrack");
        _thumbBorder = (Brush)FindResource("BrushBorderLight");
        _thumbLine = (Brush)FindResource("BrushThumbLine");
        _accent = (Brush)FindResource("BrushAccent");
        _highlight = (Brush)FindResource("BrushControlHover");
        _tickPen = new Pen(_tick, 1);
        _thumbBorderPen = new Pen(_thumbBorder, 1);
        _thumbLinePen = new Pen(_thumbLine, 2);
        _thumbLineAccentPen = new Pen(_accent, 2);
        _tickPen.Freeze(); _thumbBorderPen.Freeze(); _thumbLinePen.Freeze(); _thumbLineAccentPen.Freeze();
        _typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Regular, FontStretches.Normal);
    }

    private FormattedText Label(string s, Brush brush) => new(
        s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface!, 8.5, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        EnsureResources();
        bool active = _dragging || IsMouseOver;
        double cx = w / 2 + 2;   // 轨道中心右移,左侧留出 dB 数字刻度

        // dB 数字刻度(左缘右对齐)+ 双侧刻度线;0dB 为物理参考点,数字提亮
        foreach (double db in new[] { 10.0, 5, 0, -5, -10, -20, -30, -51 })
        {
            double y = DbToY(db);
            double ys = Math.Round(y) + 0.5;
            dc.DrawLine(_tickPen, new Point(cx - 11, ys), new Point(cx - 4, ys));
            dc.DrawLine(_tickPen, new Point(cx + 4, ys), new Point(cx + 11, ys));
            var t = Label(db > 0 ? $"+{db:0}" : $"{db:0}", db == 0 ? _textHi! : _text!);
            dc.DrawText(t, new Point(cx - 14 - t.Width, y - t.Height / 2));
        }

        // 轨道(圆角槽)
        dc.DrawRoundedRectangle(_track, null, new Rect(cx - 2, TrackTop, 4, TrackHeight), 2, 2);

        // Thumb:双色面 + 顶部高光 + 抓线(0dB 时抓线用强调色呼应刻度高亮)
        double thumbY = DbToY(Db);
        var thumb = new Rect(cx - 13, thumbY - 6, 26, 12);
        var fill = new LinearGradientBrush(
            (Color)FindResource("ColorThumbTop"), (Color)FindResource("ColorThumbBottom"), 90);
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, _thumbBorderPen, thumb, 2, 2);
        dc.DrawLine(new Pen(_highlight, 1), new Point(thumb.X + 2, thumb.Y + 1.5), new Point(thumb.Right - 2, thumb.Y + 1.5));
        var gripPen = (Db == 0 || active) ? _thumbLineAccentPen : _thumbLinePen;
        double gy = Math.Round(thumbY) + 0.5;
        dc.DrawLine(gripPen, new Point(cx - 9, gy), new Point(cx + 9, gy));

        AutomationProperties.SetName(this, "Volume fader");
    }
}

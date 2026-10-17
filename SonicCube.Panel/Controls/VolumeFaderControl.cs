using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 音量推子(dB 分段线性映射,Fluent 2 风格)。协议 dB(-51~+10)经 ValueMapping 分段映射到等距行程;
/// 拖动合并节流发送;双击复位 0;滚轮 ±1dB。
/// 视觉:左侧 dB 数字刻度(0 位高亮)、双缘刻度线、凹陷圆角轨道 + 强调色填充条、
/// Fluent 2 立体推子帽(渐变面 + 顶部高光 + 双侧凹槽 + 中心抓线)。
/// </summary>
public sealed class VolumeFaderControl : FrameworkElement
{
    public VolumeFaderControl()
    {
        Width = 50;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;
        IsEnabledChanged += (_, _) => InvalidateVisual();
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

    private double TrackTop => 7;
    private double TrackHeight => Math.Max(10, ActualHeight - 14);

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
        InvalidateVisual();
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
        InvalidateVisual();
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
    private Brush? _tick, _text, _textHi, _track, _thumbLine, _accent, _activeFill;
    private LinearGradientBrush? _thumbFill, _thumbFillHover, _thumbElevationBorder;
    private Pen? _tickPen, _zeroTickPen, _trackBorderPen, _thumbBorderPen, _thumbHighlightPen, _thumbGroovePen, _thumbLinePen, _thumbLineAccentPen;
    private Typeface? _typeface;

    private void EnsureResources()
    {
        if (_thumbLine is not null) return;
        _tick = (Brush)FindResource("BrushTickMajor");
        _text = (Brush)FindResource("BrushTextDisabled");
        _textHi = (Brush)FindResource("BrushTextSecondary");
        _track = (Brush)FindResource("BrushFaderTrack");
        _thumbLine = (Brush)FindResource("BrushThumbLine");
        _accent = (Brush)FindResource("BrushAccent");

        _activeFill = new SolidColorBrush(Color.FromArgb(0xAA, 0x4C, 0xC2, 0xFF));
        _activeFill.Freeze();

        _thumbFill = new LinearGradientBrush(
            (Color)FindResource("ColorThumbTop"), (Color)FindResource("ColorThumbBottom"), 90);
        _thumbFill.Freeze();

        _thumbFillHover = new LinearGradientBrush(
            Color.FromRgb(0x58, 0x58, 0x62), Color.FromRgb(0x3D, 0x3D, 0x45), 90);
        _thumbFillHover.Freeze();

        _thumbElevationBorder = new LinearGradientBrush(
            Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x50, 0x00, 0x00, 0x00), 90);
        _thumbElevationBorder.Freeze();

        var trackStroke = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        trackStroke.Freeze();
        _trackBorderPen = new Pen(trackStroke, 1);
        _trackBorderPen.Freeze();

        var topHighlight = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        topHighlight.Freeze();
        _thumbHighlightPen = new Pen(topHighlight, 1);
        _thumbHighlightPen.Freeze();

        var grooveBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        grooveBrush.Freeze();
        _thumbGroovePen = new Pen(grooveBrush, 1);
        _thumbGroovePen.Freeze();

        _tickPen = new Pen(_tick, 1);
        _zeroTickPen = new Pen(_textHi, 1.2);
        _thumbBorderPen = new Pen(_thumbElevationBorder, 1);
        _thumbLinePen = new Pen(_thumbLine, 2);
        _thumbLineAccentPen = new Pen(_accent, 2);
        _tickPen.Freeze(); _zeroTickPen.Freeze(); _thumbBorderPen.Freeze(); _thumbLinePen.Freeze(); _thumbLineAccentPen.Freeze();
        _typeface = new Typeface(new FontFamily("Cascadia Mono, Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
    }

    private FormattedText Label(string s, Brush brush) => new(
        s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface!, 8.5, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // 刻度为静态内容:启动时格式化一次,避免每帧 $"" 插值分配。
    private static readonly double[] TickDbs = [10.0, 5, 0, -5, -10, -20, -30, -51];
    private static readonly string[] TickLabels = TickDbs.Select(db => db > 0 ? $"+{db:0}" : $"{db:0}").ToArray();

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        EnsureResources();
        bool active = _dragging || IsMouseOver;
        double cx = w / 2 + 4;   // 轨道中心右移,左侧留出 dB 数字刻度

        // 1. dB 数字刻度(左缘右对齐)+ 双侧刻度线;0dB 为物理参考点,数字与刻度线提亮
        for (int i = 0; i < TickDbs.Length; i++)
        {
            double db = TickDbs[i];
            double y = DbToY(db);
            double ys = Math.Round(y) + 0.5;
            var pen = db == 0 ? _zeroTickPen : _tickPen;
            double tickLen = db == 0 ? 8 : 6;
            dc.DrawLine(pen, new Point(cx - 4 - tickLen, ys), new Point(cx - 4, ys));
            dc.DrawLine(pen, new Point(cx + 4, ys), new Point(cx + 4 + tickLen, ys));
            var t = Label(TickLabels[i], db == 0 ? _textHi! : _text!);
            dc.DrawText(t, new Point(cx - 14 - t.Width, y - t.Height / 2));
        }

        // 2. 轨道(深色凹槽 + 细微高光描边)
        var trackRect = new Rect(cx - 2.5, TrackTop, 5, TrackHeight);
        dc.DrawRoundedRectangle(_track, _trackBorderPen, trackRect, 2.5, 2.5);

        // 3. 行程激活填充条(从底部到当前推子位置)
        double thumbY = DbToY(Db);
        double fillH = Math.Max(0, TrackTop + TrackHeight - thumbY);
        if (fillH > 1.5 && IsEnabled)
        {
            var fillRect = new Rect(cx - 1.5, thumbY, 3, fillH);
            dc.DrawRoundedRectangle(_activeFill, null, fillRect, 1.5, 1.5);
        }

        // 4. Fluent 2 立体推子帽 (Thumb: 28×14, 圆角 3px + 顶部高光 + 上下凹槽 + 中心抓线)
        var thumb = new Rect(cx - 14, thumbY - 7, 28, 14);
        dc.DrawRoundedRectangle(active && IsEnabled ? _thumbFillHover : _thumbFill, _thumbBorderPen, thumb, 3, 3);
        dc.DrawLine(_thumbHighlightPen, new Point(thumb.X + 2.5, thumb.Y + 1.5), new Point(thumb.Right - 2.5, thumb.Y + 1.5));

        double gy = Math.Round(thumbY) + 0.5;
        dc.DrawLine(_thumbGroovePen, new Point(cx - 8, gy - 3), new Point(cx + 8, gy - 3));
        dc.DrawLine(_thumbGroovePen, new Point(cx - 8, gy + 3), new Point(cx + 8, gy + 3));

        var gripPen = (Math.Abs(Db) < 0.05 || active) ? _thumbLineAccentPen : _thumbLinePen;
        dc.DrawLine(gripPen, new Point(cx - 9, gy), new Point(cx + 9, gy));

        AutomationProperties.SetName(this, "Volume fader");
    }
}

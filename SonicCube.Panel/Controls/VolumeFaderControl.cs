using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 音量推子(dB 分段线性映射)。协议 dB(-51~+10)经 ValueMapping 分段映射到等距行程;
/// 拖动合并节流发送;双击复位 0;滚轮 ±1dB;右侧绘制分段刻度。
/// 数值文本由通道条在推子底部单独呈现(较低频率更新)。
/// </summary>
public sealed class VolumeFaderControl : FrameworkElement
{
    public VolumeFaderControl()
    {
        Width = 44;
        SnapsToDevicePixels = true;
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

    public void SetDbSilently(double db) => SetCurrentValue(DbProperty, Math.Clamp(db, MinDb, MaxDb));

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
            SetDbSilently(0);
            QueueSend(0);
            FlushPending();
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
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double nd = Math.Clamp(Db + (e.Delta > 0 ? 1 : -1), MinDb, MaxDb);
        SetDbSilently(nd);
        QueueSend(nd);
        FlushPending();
        e.Handled = true;
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
            SetDbSilently(nd);
            QueueSend(nd);
            FlushPending();
            e.Handled = true;
        }
    }

    private void UpdateFromY(double y)
    {
        double nd = Math.Clamp(YToDb(y), MinDb, MaxDb);
        if (Math.Abs(nd - Db) < 0.05) return;
        SetDbSilently(nd);
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
        ValueCommitted?.Invoke(_lastSentDb);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        double cx = w / 2;

        // 刻度(分段等距)
        var tickPen = new Pen((Brush)FindResource("BrushBorderLight"), 1);
        foreach (double db in new[] { 10.0, 5, 0, -5, -10, -20, -30, -51 })
        {
            double y = DbToY(db);
            dc.DrawLine(tickPen, new Point(cx - 10, y), new Point(cx - 4, y));
            dc.DrawLine(tickPen, new Point(cx + 4, y), new Point(cx + 10, y));
        }
        // 轨道
        dc.DrawRectangle((Brush)FindResource("BrushPanelDeep"), null,
            new Rect(cx - 2, TrackTop, 4, TrackHeight));

        // 滑块
        double thumbY = DbToY(Db);
        var thumb = new Rect(cx - 13, thumbY - 6, 26, 12);
        dc.DrawRoundedRectangle((Brush)FindResource("BrushControl"), new Pen((Brush)FindResource("BrushBorderLight"), 1), thumb, 2, 2);
        dc.DrawLine(new Pen((Brush)FindResource("BrushAccent"), 2), new Point(cx - 9, thumbY), new Point(cx + 9, thumbY));

        AutomationProperties.SetName(this, "Volume fader");
    }
}

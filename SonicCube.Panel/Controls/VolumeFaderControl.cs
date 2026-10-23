using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 音量推子(dB 分段线性映射,Fluent 2 风格)。协议 dB(-51~+10)经 ValueMapping 分段映射到等距行程;
/// 拖动合并节流发送;双击复位 0;滚轮 ±1dB。
/// 视觉:左侧 dB 数字刻度(0 位高亮)、双缘刻度线、圆角轨道 + 系统强调色填充条、
/// 中性推子帽与清晰的悬停、键盘焦点反馈。
/// </summary>
public sealed class VolumeFaderControl : FrameworkElement
{
    public VolumeFaderControl()
    {
        Width = 50;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Volume fader");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelDrag(); InvalidateVisual(); };
        Unloaded += (_, _) => CancelDrag();
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    public static readonly DependencyProperty DbProperty = DependencyProperty.Register(
        nameof(Db), typeof(double), typeof(VolumeFaderControl),
        new FrameworkPropertyMetadata(-51.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnDbChanged));
    /// <summary>协议 dB 值。</summary>
    public double Db { get => (double)GetValue(DbProperty); set => SetValue(DbProperty, value); }

    /// <summary>节流后提交协议 dB。</summary>
    public event Action<double>? ValueCommitted;
    /// <summary>当前显示值变化(拖动、键盘、滚轮及远端同步共用,用于数值标签)。</summary>
    public event Action<double>? ValueChanging;

    public const double MinDb = -51;
    public const double MaxDb = 10;

    private bool _dragging;
    private double _pendingDb = double.NaN;
    private double _authoritativeDb = MinDb;
    private System.Windows.Threading.DispatcherTimer? _sendTimer;

    private static void OnDbChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (VolumeFaderControl)d;
        double v = Math.Clamp((double)e.NewValue, MinDb, MaxDb);
        if (!Equals(e.NewValue, v)) { f.SetCurrentValue(DbProperty, v); return; }
        f.InvalidateVisual();
        f.ValueChanging?.Invoke(v);
    }

    public void SetDbSilently(double db)
    {
        _authoritativeDb = Math.Clamp(db, MinDb, MaxDb);
        if (StateSynchronization.IsLocal(this)) _awaitingConfirmDb = double.NaN;
        else if (!double.IsNaN(_awaitingConfirmDb))
        {
            if (Math.Abs(db - _awaitingConfirmDb) < 0.05) _awaitingConfirmDb = double.NaN; // 回包确认,恢复
            else return; // 旧快照,忽略
        }
        if (!_dragging && double.IsNaN(_pendingDb)) SetDbInternal(_authoritativeDb);
    }

    private void SetDbInternal(double db) => SetCurrentValue(DbProperty, Math.Clamp(db, MinDb, MaxDb));
    private double _awaitingConfirmDb = double.NaN;

    /// <summary>调用方按请求代次过滤过期结果;此处再核对目标值,避免清除较新的拖动提交。</summary>
    public void CompletePendingWrite(double expectedValue, bool success)
    {
        if (double.IsNaN(_awaitingConfirmDb) || Math.Abs(expectedValue - _awaitingConfirmDb) >= 0.05) return;
        _awaitingConfirmDb = double.NaN;
        if (success) _authoritativeDb = Math.Clamp(expectedValue, MinDb, MaxDb);
        if (!_dragging && double.IsNaN(_pendingDb)) SetDbInternal(_authoritativeDb);
    }

    public void ResetSynchronization()
    {
        _awaitingConfirmDb = double.NaN;
        CancelDrag();
    }

    private void CancelDrag()
    {
        _dragging = false;
        _sendTimer?.Stop();
        _pendingDb = double.NaN;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetDbInternal(double.IsNaN(_awaitingConfirmDb) ? _authoritativeDb : _awaitingConfirmDb);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) CancelDrag();
    }

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
            e.Handled = true;
        }
    }

    private void UpdateFromY(double y)
    {
        double nd = Math.Clamp(YToDb(y), MinDb, MaxDb);
        if (Math.Abs(nd - Db) < 0.05) return;
        SetDbInternal(nd);
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
        double value = _pendingDb;
        _pendingDb = double.NaN;
        double comparison = double.IsNaN(_awaitingConfirmDb) ? _authoritativeDb : _awaitingConfirmDb;
        if (Math.Abs(value - comparison) < 0.01) return;
        _awaitingConfirmDb = value;
        ValueCommitted?.Invoke(value);
    }

    // ── 画刷/文字资源缓存 ──
    private Brush? _tick, _text, _textHi, _track, _thumbLine, _accent, _activeFill;
    private Brush? _thumbFill, _thumbFillHover;
    private Pen? _tickPen, _zeroTickPen, _trackBorderPen, _thumbBorderPen, _thumbLinePen, _thumbLineAccentPen, _focusPen;
    private Typeface? _typeface;

    private void EnsureResources()
    {
        if (_thumbLine is not null) return;
        _tick = this.GetDrawingBrush("BrushTickMajor");
        _text = this.GetDrawingBrush("BrushTextTertiary");
        _textHi = this.GetDrawingBrush("BrushTextSecondary");
        _track = this.GetDrawingBrush("BrushFaderTrack");
        _thumbLine = this.GetDrawingBrush("BrushThumbLine");
        _accent = this.GetDrawingBrush("BrushAccent");

        _activeFill = _accent.Clone();
        _activeFill.Opacity = 0.75;
        _activeFill.Freeze();

        _thumbFill = new SolidColorBrush((Color)FindResource("ColorThumbBottom"));
        _thumbFill.Freeze();

        _thumbFillHover = new SolidColorBrush((Color)FindResource("ColorThumbTop"));
        _thumbFillHover.Freeze();

        var trackStroke = this.GetDrawingBrush("BrushBorderLight");
        _trackBorderPen = new Pen(trackStroke, 1);
        _trackBorderPen.Freeze();

        _tickPen = new Pen(_tick, 1);
        _zeroTickPen = new Pen(_textHi, 1.2);
        _thumbBorderPen = new Pen(trackStroke, 1);
        _thumbLinePen = new Pen(_thumbLine, 2);
        _thumbLineAccentPen = new Pen(_accent, 2);
        _focusPen = new Pen(_accent, 1.5);
        _tickPen.Freeze(); _zeroTickPen.Freeze(); _thumbBorderPen.Freeze(); _thumbLinePen.Freeze(); _thumbLineAccentPen.Freeze(); _focusPen.Freeze();
        _typeface = new Typeface((FontFamily)FindResource("FontMono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    // 刻度为静态内容:启动时格式化一次,并按尺寸/DPI 预烘焙静态刻度与轨道背景。
    private static readonly double[] TickDbs = [10.0, 5, 0, -5, -10, -20, -30, -51];
    private static readonly string[] TickLabels = TickDbs.Select(db => db > 0 ? $"+{db:0}" : $"{db:0}").ToArray();
    private double _cachedW, _cachedH, _cachedDpi;
    private DrawingGroup? _staticBackdrop;

    private void EnsureStaticCache(double w, double h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_staticBackdrop is not null
            && Math.Abs(w - _cachedW) < 0.05
            && Math.Abs(h - _cachedH) < 0.05
            && Math.Abs(dpi - _cachedDpi) < 0.001)
            return;

        _cachedW = w;
        _cachedH = h;
        _cachedDpi = dpi;

        double cx = w / 2 + 4;
        var backdrop = new DrawingGroup();
        using (var dc = backdrop.Open())
        {
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
                var t = new FormattedText(TickLabels[i], System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _typeface!, 8.5, db == 0 ? _textHi! : _text!, dpi);
                dc.DrawText(t, new Point(cx - 14 - t.Width, y - t.Height / 2));
            }

            // 2. 轨道(深色凹槽 + 细微高光描边)
            var trackRect = new Rect(cx - 2.5, TrackTop, 5, TrackHeight);
            dc.DrawRoundedRectangle(_track, _trackBorderPen, trackRect, 2.5, 2.5);
        }
        backdrop.Freeze();
        _staticBackdrop = backdrop;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        EnsureResources();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        EnsureStaticCache(w, h);
        bool active = _dragging || IsMouseOver;
        double cx = w / 2 + 4;   // 轨道中心右移,左侧留出 dB 数字刻度

        dc.DrawDrawing(_staticBackdrop);

        // 3. 行程激活填充条(从底部到当前推子位置)
        double thumbY = DbToY(Db);
        double fillH = Math.Max(0, TrackTop + TrackHeight - thumbY);
        if (fillH > 1.5 && IsEnabled)
        {
            var fillRect = new Rect(cx - 1.5, thumbY, 3, fillH);
            dc.DrawRoundedRectangle(_activeFill, null, fillRect, 1.5, 1.5);
        }

        // 4. 中性推子帽 (28×14)，保留一条清晰抓线。
        var thumb = new Rect(cx - 14, thumbY - 7, 28, 14);
        dc.DrawRoundedRectangle(active && IsEnabled ? _thumbFillHover : _thumbFill,
            IsKeyboardFocused && IsEnabled ? _focusPen : _thumbBorderPen, thumb, 3, 3);

        double gy = Math.Round(thumbY) + 0.5;

        var gripPen = IsEnabled && (Math.Abs(Db) < 0.05 || active) ? _thumbLineAccentPen : _thumbLinePen;
        dc.DrawLine(gripPen, new Point(cx - 9, gy), new Point(cx + 9, gy));
    }
}

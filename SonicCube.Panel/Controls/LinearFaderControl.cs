using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>线性垂直滑条(min~max 线性,双击回默认,Fluent 2 风格)。用于 Global EQ 总音量等线性参数。</summary>
public sealed class LinearFaderControl : FrameworkElement
{
    public LinearFaderControl()
    {
        Width = 38; Height = 130;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Level");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelDrag(); InvalidateVisual(); };
        Unloaded += (_, _) => CancelDrag();
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LinearFaderControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(LinearFaderControl), new PropertyMetadata(-50.0, OnRangeChanged));
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LinearFaderControl), new PropertyMetadata(50.0, OnRangeChanged));
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(LinearFaderControl), new PropertyMetadata(0.0));
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }

    public event Action<double>? ValueCommitted;
    public event Action<double>? ValueChanging;

    private bool _dragging;
    private double _synchronizedValue;
    private DateTime _suppressEchoUntil;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (LinearFaderControl)d;
        double v = Math.Clamp((double)e.NewValue, f.Minimum, f.Maximum);
        if (!Equals(e.NewValue, v)) { f.SetCurrentValue(ValueProperty, v); return; }
        f.InvalidateVisual();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (LinearFaderControl)d;
        f.SetCurrentValue(ValueProperty, Math.Clamp(f.Value, f.Minimum, f.Maximum));
        f.InvalidateVisual();
    }

    public void SetValueSilently(double v)
    {
        _synchronizedValue = Math.Clamp(v, Minimum, Maximum);
        if (_dragging || (!StateSynchronization.IsLocal(this) && DateTime.UtcNow < _suppressEchoUntil)) return;
        SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));
    }

    private void SetValueInternal(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));
    private void SuppressEchoBriefly() => _suppressEchoUntil = DateTime.UtcNow.AddMilliseconds(600);

    public void FailPendingWrite(double expected)
    {
        if (_dragging || (float)Value != (float)expected) return;
        _suppressEchoUntil = DateTime.MinValue;
        SetValueInternal(_synchronizedValue);
        ValueChanging?.Invoke(Value);
    }

    private void CancelDrag()
    {
        _dragging = false;
        _suppressEchoUntil = DateTime.MinValue;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetValueInternal(_synchronizedValue);
        ValueChanging?.Invoke(Value);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) CancelDrag();
    }

    private double TrackTop => 7;
    private double TrackHeight => Math.Max(10, ActualHeight - 14);

    // ── 画笔/画刷缓存 ──
    private Brush? _track, _accent, _activeFill;
    private Brush? _thumbFill, _thumbFillHover;
    private Pen? _trackBorderPen, _tickPen, _thumbPen, _accentPen, _linePen, _focusPen;

    private void EnsureResources()
    {
        if (_thumbPen is not null) return;
        _track = this.GetDrawingBrush("BrushFaderTrack");
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

        _tickPen = new Pen(this.GetDrawingBrush("BrushTickMajor"), 1);
        _thumbPen = new Pen(trackStroke, 1);
        _accentPen = new Pen(_accent, 2);
        _focusPen = new Pen(_accent, 1.5);
        _linePen = new Pen(this.GetDrawingBrush("BrushThumbLine"), 2);
        _tickPen.Freeze(); _thumbPen.Freeze(); _accentPen.Freeze(); _linePen.Freeze(); _focusPen.Freeze();
    }

    private double YToValue(double y)
    {
        double t = 1 - (y - TrackTop) / TrackHeight;
        return Math.Round(Minimum + Math.Clamp(t, 0, 1) * (Maximum - Minimum));
    }

    private double ValueToY(double v)
    {
        double t = (v - Minimum) / (Maximum - Minimum);
        return TrackTop + (1 - t) * TrackHeight;
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            SetValueInternal(DefaultValue);
            SuppressEchoBriefly();
            ValueChanging?.Invoke(DefaultValue);
            ValueCommitted?.Invoke(DefaultValue);
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        Apply(e.GetPosition(this).Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Apply(e.GetPosition(this).Y);
    }

    private void Apply(double y)
    {
        double nv = YToValue(y);
        if (Math.Abs(nv - Value) < 0.5) return;
        SetValueInternal(nv);
        ValueChanging?.Invoke(nv);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        SuppressEchoBriefly();
        InvalidateVisual();
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double nv = Math.Clamp(Value + (e.Delta > 0 ? 1 : -1), Minimum, Maximum);
        if (Math.Abs(nv - Value) < 0.5) return;
        SetValueInternal(nv);
        SuppressEchoBriefly();
        ValueChanging?.Invoke(nv);
        ValueCommitted?.Invoke(nv);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double nv = e.Key switch
        {
            Key.Up => Value + 1,
            Key.Down => Value - 1,
            Key.Home => Maximum,
            Key.End => Minimum,
            _ => Value,
        };
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End)
        {
            nv = Math.Clamp(nv, Minimum, Maximum);
            SetValueInternal(nv);
            SuppressEchoBriefly();
            ValueChanging?.Invoke(nv);
            ValueCommitted?.Invoke(nv);
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        EnsureResources();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        bool active = _dragging || IsMouseOver;
        double cx = w / 2;

        // 中心 0 位刻度线
        double midY = Math.Round(ValueToY(DefaultValue)) + 0.5;
        dc.DrawLine(_tickPen, new Point(cx - 12, midY), new Point(cx + 12, midY));

        // 轨道
        dc.DrawRoundedRectangle(_track, _trackBorderPen, new Rect(cx - 2.5, TrackTop, 5, TrackHeight), 2.5, 2.5);

        // 双极填充(从中心默认值到当前值)
        double y = ValueToY(Value);
        double topY = Math.Min(y, midY);
        double fillH = Math.Abs(y - midY);
        if (fillH > 1 && IsEnabled)
            dc.DrawRoundedRectangle(_activeFill, null, new Rect(cx - 1.5, topY, 3, fillH), 1.5, 1.5);

        // 中性推子帽与独立键盘焦点轮廓
        var thumb = new Rect(cx - 13, y - 6.5, 26, 13);
        dc.DrawRoundedRectangle(active && IsEnabled ? _thumbFillHover : _thumbFill,
            IsKeyboardFocused && IsEnabled ? _focusPen : _thumbPen, thumb, 3, 3);
        var gripPen = IsEnabled && (Math.Abs(Value - DefaultValue) < 0.5 || active) ? _accentPen : _linePen;
        double gy = Math.Round(y) + 0.5;
        dc.DrawLine(gripPen, new Point(cx - 8, gy), new Point(cx + 8, gy));
    }
}

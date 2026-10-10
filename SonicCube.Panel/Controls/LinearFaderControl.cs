using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>线性垂直滑条(min~max 线性,双击回默认)。用于 Global EQ 总音量等线性参数。</summary>
public sealed class LinearFaderControl : FrameworkElement
{
    public LinearFaderControl()
    {
        Width = 36; Height = 150;
        SnapsToDevicePixels = true;
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

    public void SetValueSilently(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));

    private double TrackTop => 4;
    private double TrackHeight => Math.Max(10, ActualHeight - 8);

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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            SetValueSilently(DefaultValue);
            ValueCommitted?.Invoke(DefaultValue);
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        Apply(e.GetPosition(this).Y);
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
        SetValueSilently(nv);
        ValueChanging?.Invoke(nv);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        ValueCommitted?.Invoke(Value);
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
            SetValueSilently(nv);
            ValueCommitted?.Invoke(nv);
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        double cx = w / 2;
        dc.DrawRectangle((Brush)FindResource("BrushPanelDeep"), null, new Rect(cx - 2, TrackTop, 4, TrackHeight));
        double y = ValueToY(Value);
        var thumb = new Rect(cx - 13, y - 6, 26, 12);
        dc.DrawRoundedRectangle((Brush)FindResource("BrushControl"), new Pen((Brush)FindResource("BrushBorderLight"), 1), thumb, 2, 2);
        dc.DrawLine(new Pen((Brush)FindResource("BrushAccent"), 2), new Point(cx - 9, y), new Point(cx + 9, y));
        AutomationProperties.SetName(this, "Level");
    }
}

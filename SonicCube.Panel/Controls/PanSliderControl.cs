using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 声像 L-R 水平滑条(float -50~50 直发,无换算)。双击复位 0。
/// mono 关闭时由父级置 IsEnabled=false(禁用视觉与“关闭”区分:禁用=灰,关闭=未选中)。
/// </summary>
public sealed class PanSliderControl : FrameworkElement
{
    public PanSliderControl()
    {
        Height = 30;
        SnapsToDevicePixels = true;
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PanSliderControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    /// <summary>-50~50。</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public event Action<double>? ValueCommitted;
    public event Action<double>? ValueChanging;

    private bool _dragging;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (PanSliderControl)d;
        double v = Math.Clamp((double)e.NewValue, -50, 50);
        if (!Equals(e.NewValue, v)) { p.SetCurrentValue(ValueProperty, v); return; }
        p.InvalidateVisual();
    }

    public void SetValueSilently(double v)
    {
        // 拖动中或刚提交后短暂窗口内不回显,避免快照回跳
        if (_dragging || DateTime.UtcNow < _suppressEchoUntil) return;
        SetCurrentValue(ValueProperty, Math.Clamp(v, -50, 50));
    }

    private void SetValueInternal(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, -50, 50));
    private DateTime _suppressEchoUntil = DateTime.MinValue;
    private void SuppressEchoBriefly() => _suppressEchoUntil = DateTime.UtcNow.AddMilliseconds(600);

    private double XToValue(double x)
    {
        double t = Math.Clamp((x - 8.0) / (ActualWidth - 16.0), 0, 1);
        return Math.Round(t * 100 - 50);
    }

    private double ValueToX(double v) => 8 + (v + 50) / 100.0 * (ActualWidth - 16);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!IsEnabled) return;
        Focus();
        if (e.ClickCount == 2)
        {
            SetValueInternal(0);
            ValueCommitted?.Invoke(0);
            SuppressEchoBriefly();
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        ApplyX(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ApplyX(e.GetPosition(this).X);
    }

    private void ApplyX(double x)
    {
        double nv = XToValue(x);
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
        ValueCommitted?.Invoke(Value);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!IsEnabled) return;
        double nv = Math.Clamp(Value + (e.Delta > 0 ? 1 : -1), -50, 50);
        SetValueInternal(nv);
        SuppressEchoBriefly();
        ValueCommitted?.Invoke(nv);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double nv = e.Key switch
        {
            Key.Left => Value - 1,
            Key.Right => Value + 1,
            Key.Home => -50,
            Key.End => 50,
            _ => Value,
        };
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            nv = Math.Clamp(nv, -50, 50);
            SetValueInternal(nv);
            SuppressEchoBriefly();
            ValueCommitted?.Invoke(nv);
            e.Handled = true;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        double cy = h / 2 + 4;

        var trackPen = new Pen((Brush)FindResource("BrushBorderLight"), 2);
        dc.DrawLine(trackPen, new Point(8, cy), new Point(w - 8, cy));
        // 中线
        dc.DrawLine(new Pen((Brush)FindResource("BrushBorder"), 1), new Point(w / 2, cy - 5), new Point(w / 2, cy + 5));

        double x = ValueToX(Value);
        var thumbBrush = IsEnabled ? (Brush)FindResource("BrushAccent") : (Brush)FindResource("BrushOff");
        dc.DrawRoundedRectangle(thumbBrush, new Pen((Brush)FindResource("BrushBorderLight"), 1), new Rect(x - 6, cy - 8, 12, 16), 2, 2);

        // 顶部 L / 值 / R
        var brush = (Brush)FindResource(IsEnabled ? "BrushTextSecondary" : "BrushTextDisabled");
        string txt = Value == 0 ? "C" : (Value < 0 ? $"L{-Value:0}" : $"R{Value:0}");
        DrawText(dc, "L", new Rect(0, 0, 12, 12), brush, 9);
        DrawText(dc, "R", new Rect(w - 12, 0, 12, 12), brush, 9);
        DrawText(dc, txt, new Rect(0, 0, w, 12), brush, 9);

        AutomationProperties.SetName(this, "Pan");
    }

    private void DrawText(DrawingContext dc, string text, Rect area, Brush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(area.X + (area.Width - ft.Width) / 2, area.Y));
    }
}

using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 声像 L-R 水平滑条(float -50~50 直发,无换算,Fluent 2 风格)。双击复位 0。
/// mono 关闭时由父级置 IsEnabled=false(禁用视觉与“关闭”区分:禁用=灰,关闭=未选中)。
/// </summary>
public sealed class PanSliderControl : FrameworkElement
{
    public PanSliderControl()
    {
        Height = 32;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Pan");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelDrag(); InvalidateVisual(); };
        Unloaded += (_, _) => CancelDrag();
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        LostKeyboardFocus += (_, _) => InvalidateVisual();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(PanSliderControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    /// <summary>-50~50。</summary>
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public event Action<double>? ValueCommitted;
    public event Action<double>? ValueChanging;

    private bool _dragging;
    private double _synchronizedValue;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var p = (PanSliderControl)d;
        double v = Math.Clamp((double)e.NewValue, -50, 50);
        if (!Equals(e.NewValue, v)) { p.SetCurrentValue(ValueProperty, v); return; }
        p.InvalidateVisual();
    }

    public void SetValueSilently(double v)
    {
        _synchronizedValue = Math.Clamp(v, -50, 50);
        // 拖动中或刚提交后短暂窗口内不回显,避免快照回跳
        if (_dragging || (!StateSynchronization.IsLocal(this) && DateTime.UtcNow < _suppressEchoUntil)) return;
        SetCurrentValue(ValueProperty, Math.Clamp(v, -50, 50));
    }

    private void SetValueInternal(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, -50, 50));
    private DateTime _suppressEchoUntil = DateTime.MinValue;
    private void SuppressEchoBriefly() => _suppressEchoUntil = DateTime.UtcNow.AddMilliseconds(600);

    public void FailPendingWrite(double expected)
    {
        if (_dragging || (float)Value != (float)expected) return;
        _suppressEchoUntil = DateTime.MinValue;
        SetValueInternal(_synchronizedValue);
    }

    private void CancelDrag()
    {
        _dragging = false;
        _suppressEchoUntil = DateTime.MinValue;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetValueInternal(_synchronizedValue);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) CancelDrag();
    }

    private double XToValue(double x)
    {
        double t = Math.Clamp((x - 8.0) / Math.Max(10, ActualWidth - 16.0), 0, 1);
        return Math.Round(t * 100 - 50);
    }

    // ── 画笔/画刷缓存 ──
    private Pen? _midPen, _thumbPen, _trackBorderPen, _focusPen;
    private Brush? _trackBrush, _brushAccent, _brushAccentHover, _brushOff;
    private Typeface? _typeface;

    private void EnsureResources()
    {
        if (_midPen is not null) return;
        _trackBrush = this.GetDrawingBrush("BrushFaderTrack");
        _brushAccent = this.GetDrawingBrush("BrushAccent");
        _brushAccentHover = this.GetDrawingBrush("BrushAccentHover");
        _brushOff = this.GetDrawingBrush("BrushOff");

        var trackStroke = this.GetDrawingBrush("BrushBorderLight");
        _trackBorderPen = new Pen(trackStroke, 1);
        _trackBorderPen.Freeze();

        _midPen = new Pen(this.GetDrawingBrush("BrushTickMajor"), 1);
        _thumbPen = new Pen(this.GetDrawingBrush("BrushBorderLight"), 1);
        _focusPen = new Pen(this.GetDrawingBrush("BrushText"), 1.5);
        _midPen.Freeze(); _thumbPen.Freeze(); _focusPen.Freeze();
        _typeface = new Typeface((FontFamily)FindResource("FontMono"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    }

    private double ValueToX(double v) => 8 + (v + 50) / 100.0 * Math.Max(10, ActualWidth - 16);

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
        if (!IsEnabled) return;
        Focus();
        if (e.ClickCount == 2)
        {
            SetValueInternal(0);
            SuppressEchoBriefly();
            ValueCommitted?.Invoke(0);
            e.Handled = true;
            return;
        }
        _dragging = true;
        CaptureMouse();
        ApplyX(e.GetPosition(this).X);
        InvalidateVisual();
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
        InvalidateVisual();
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
        if (w < 16 || h < 12) return;
        EnsureResources();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        bool active = _dragging || IsMouseOver;
        double cy = h - 9;
        double midX = w / 2;

        // 1. 顶部 L / 值 / R 标签
        var labelBrush = (Brush)FindResource(IsEnabled ? "BrushTextSecondary" : "BrushTextDisabled");
        var valBrush = (Brush)FindResource(IsEnabled ? "BrushText" : "BrushTextDisabled");
        string txt = Value == 0 ? "C" : (Value < 0 ? $"L{-Value:0}" : $"R{Value:0}");
        DrawText(dc, "L", new Rect(2, 0, 14, 13), labelBrush, 9.5);
        DrawText(dc, "R", new Rect(w - 16, 0, 14, 13), labelBrush, 9.5);
        DrawText(dc, txt, new Rect(16, 0, w - 32, 13), valBrush, 10);

        // 2. 凹槽圆角轨道 (高 4px)
        dc.DrawRoundedRectangle(_trackBrush, _trackBorderPen, new Rect(8, cy - 2, w - 16, 4), 2, 2);

        // 3. 中心刻度线
        double ms = Math.Round(midX) + 0.5;
        dc.DrawLine(_midPen, new Point(ms, cy - 6), new Point(ms, cy + 6));

        // 4. 双极偏移填充(从中心 C 到当前位置 x)
        double x = ValueToX(Value);
        if (IsEnabled && Math.Abs(x - midX) > 1.5)
        {
            double left = Math.Min(x, midX);
            double width = Math.Abs(x - midX);
            dc.DrawRoundedRectangle(_brushAccent, null, new Rect(left, cy - 1.5, width, 3), 1.5, 1.5);
        }

        // 5. Fluent 2 圆角滑块帽
        var thumbBrush = !IsEnabled ? _brushOff : (active ? _brushAccentHover : _brushAccent);
        dc.DrawRoundedRectangle(thumbBrush, _thumbPen, new Rect(x - 5.5, cy - 6.5, 11, 13), 3, 3);
        if (IsKeyboardFocused && IsEnabled)
            dc.DrawRoundedRectangle(null, _focusPen, new Rect(x - 7.5, cy - 8.5, 15, 17), 5, 5);
    }

    private void DrawText(DrawingContext dc, string text, Rect area, Brush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface!, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(area.X + (area.Width - ft.Width) / 2, area.Y));
    }
}

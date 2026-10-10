using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 参数旋钮(自绘)。拖拽步进 = 0.01×(max-min),取整到 step;滚轮 ±step;
/// Alt+单击或双击回默认值;点击数值文本可键盘输入(clamp 到 min/max)。
/// 值直发协议原值,无额外换算(换算在调用方)。
/// </summary>
public sealed class KnobControl : FrameworkElement
{
    static KnobControl()
    {
        FocusableProperty.OverrideMetadata(typeof(KnobControl), new FrameworkPropertyMetadata(true));
    }

    public KnobControl()
    {
        Width = 74; Height = 98;
        _editor.Visibility = Visibility.Collapsed;
        _editor.TextAlignment = TextAlignment.Center;
        _editor.LostFocus += (_, _) => CommitEditor();
        _editor.KeyDown += OnEditorKeyDown;
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(KnobControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(KnobControl), new PropertyMetadata(0.0, OnRangeChanged));
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(KnobControl), new PropertyMetadata(100.0, OnRangeChanged));
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(KnobControl), new PropertyMetadata(1.0));
    public double Step { get => (double)GetValue(StepProperty); set => SetValue(StepProperty, value); }

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(KnobControl), new PropertyMetadata(0.0));
    public double DefaultValue { get => (double)GetValue(DefaultValueProperty); set => SetValue(DefaultValueProperty, value); }

    public static readonly DependencyProperty ParamNameProperty = DependencyProperty.Register(
        nameof(ParamName), typeof(string), typeof(KnobControl), new PropertyMetadata("", (d, _) => ((KnobControl)d).InvalidateVisual()));
    public string ParamName { get => (string)GetValue(ParamNameProperty); set => SetValue(ParamNameProperty, value); }

    public event Action<double>? ValueCommitted;
    public event Action<double>? ValueChanging;

    private readonly TextBox _editor = new();
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartValue;

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var k = (KnobControl)d;
        double v = Math.Clamp((double)e.NewValue, k.Minimum, k.Maximum);
        if (!Equals(e.NewValue, v)) { k.SetCurrentValue(ValueProperty, v); return; }
        k.InvalidateVisual();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var k = (KnobControl)d;
        k.SetCurrentValue(ValueProperty, Math.Clamp(k.Value, k.Minimum, k.Maximum));
        k.InvalidateVisual();
    }

    public void SetValueSilently(double v)
    {
        // 拖动中或刚提交后短暂窗口内不回显,避免快照覆盖用户输入/造成回跳
        if (_dragging || DateTime.UtcNow < _suppressEchoUntil) return;
        SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));
    }

    /// <summary>内部直接设置(拖动/提交路径用,不受回显抑制影响)。</summary>
    private void SetValueInternal(double v) => SetCurrentValue(ValueProperty, Math.Clamp(v, Minimum, Maximum));

    private DateTime _suppressEchoUntil = DateTime.MinValue;
    private void SuppressEchoBriefly() => _suppressEchoUntil = DateTime.UtcNow.AddMilliseconds(600);

    public void Configure(double min, double max, double step, double def, string name)
    {
        Minimum = min; Maximum = max; Step = step; DefaultValue = def; ParamName = name;
        SetValueSilently(def);
    }

    private double Snap(double v) => Step >= 1 ? Math.Round(v) : Math.Round(v / Step) * Step;

    private void ResetToDefault()
    {
        SetValueInternal(DefaultValue);
        ValueCommitted?.Invoke(Value);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2) { ResetToDefault(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ResetToDefault(); e.Handled = true; return; }
        // 命中数值区域→进入编辑
        var p = e.GetPosition(this);
        if (p.Y > ActualHeight - 20)
        {
            BeginEdit(e);
            return;
        }
        _dragging = true;
        CaptureMouse();
        _dragStartY = p.Y;
        _dragStartValue = Value;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        double range = Maximum - Minimum;
        double delta = (_dragStartY - e.GetPosition(this).Y) * 0.01 * range;
        double nv = Snap(Math.Clamp(_dragStartValue + delta, Minimum, Maximum));
        if (Math.Abs(nv - Value) < 1e-9) return;
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
        double nv = Snap(Math.Clamp(Value + (e.Delta > 0 ? Step : -Step), Minimum, Maximum));
        if (Math.Abs(nv - Value) < 1e-9) return;
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
            Key.Up or Key.Right => Snap(Value + Step),
            Key.Down or Key.Left => Snap(Value - Step),
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => Value,
        };
        if (e.Key is Key.Up or Key.Down or Key.Left or Key.Right or Key.Home or Key.End)
        {
            nv = Math.Clamp(nv, Minimum, Maximum);
            if (Math.Abs(nv - Value) >= 1e-9) { SetValueInternal(nv); ValueCommitted?.Invoke(nv); }
            e.Handled = true;
        }
    }

    private void BeginEdit(MouseButtonEventArgs e)
    {
        _editor.Text = Step >= 1 ? Value.ToString("0") : Value.ToString("0.##");
        if (_editor.Parent == null)
        {
            var host = VisualTreeHelper.GetParent(this) as System.Windows.Controls.Panel;
            host?.Children.Add(_editor);
            if (host != null) Canvas.SetZIndex(_editor, 1000);
        }
        if (_editor.Parent is System.Windows.Controls.Panel panel)
        {
            var pos = TranslatePoint(new Point(0, ActualHeight - 20), panel);
            _editor.Width = ActualWidth;
            _editor.Height = 18;
            _editor.Margin = new Thickness(pos.X, pos.Y, 0, 0);
            _editor.HorizontalAlignment = HorizontalAlignment.Left;
            _editor.VerticalAlignment = VerticalAlignment.Top;
            _editor.Visibility = Visibility.Visible;
            _editor.Focus();
            _editor.SelectAll();
        }
        e.Handled = true;
    }

    private void OnEditorKeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CommitEditor();
        else if (e.Key == Key.Escape) _editor.Visibility = Visibility.Collapsed;
    }

    private void CommitEditor()
    {
        if (_editor.Visibility != Visibility.Visible) return;
        if (double.TryParse(_editor.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            v = Snap(Math.Clamp(v, Minimum, Maximum));
            SetValueInternal(v);
            ValueCommitted?.Invoke(v);
        }
        _editor.Visibility = Visibility.Collapsed;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        double nameH = 14, valueH = 18;
        double knobH = h - nameH - valueH;
        double cx = w / 2, cy = nameH + knobH / 2;
        double radius = Math.Min(w, knobH) / 2 - 3;

        // 名称(上)
        DrawText(dc, ParamName, new Rect(0, 0, w, nameH), "BrushTextSecondary", 11);

        // 底盘
        var body = (Brush)FindResource("BrushControl");
        var border = (Brush)FindResource("BrushBorderLight");
        dc.DrawEllipse(body, new Pen(border, 1), new Point(cx, cy), radius, radius);

        double t = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        t = Math.Clamp(t, 0, 1);

        DrawArc(dc, cx, cy, radius - 2, 135, 270, new Pen((Brush)FindResource("BrushOff"), 3));
        if (t > 0.002)
            DrawArc(dc, cx, cy, radius - 2, 135, 270 * t, new Pen((Brush)FindResource("BrushAccent"), 3));

        double ang = (135 + 270 * t) * Math.PI / 180.0;
        var pOuter = new Point(cx + Math.Cos(ang) * (radius - 6), cy + Math.Sin(ang) * (radius - 6));
        var pInner = new Point(cx + Math.Cos(ang) * (radius * 0.25), cy + Math.Sin(ang) * (radius * 0.25));
        dc.DrawLine(new Pen(IsEnabled ? Brushes.White : (Brush)FindResource("BrushTextDisabled"), 2.5), pInner, pOuter);
        dc.DrawEllipse((Brush)FindResource("BrushPanelDeep"), null, new Point(cx, cy), 3.5, 3.5);

        // 数值(下)
        string txt = Step >= 1 ? Value.ToString("0") : Value.ToString("0.##");
        DrawText(dc, txt, new Rect(0, h - valueH, w, valueH), "BrushText", 12);

        AutomationProperties.SetName(this, string.IsNullOrEmpty(ParamName) ? "Knob" : ParamName);
    }

    private void DrawText(DrawingContext dc, string text, Rect area, string brushKey, double size)
    {
        var brush = (Brush)FindResource(brushKey);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ft.MaxTextWidth = area.Width;
        dc.DrawText(ft, new Point(area.X + (area.Width - ft.Width) / 2, area.Y + (area.Height - ft.Height) / 2));
    }

    private static void DrawArc(DrawingContext dc, double cx, double cy, double r, double startDeg, double sweepDeg, Pen pen)
    {
        int segs = Math.Max(2, (int)(Math.Abs(sweepDeg) / 6));
        double a0 = startDeg * Math.PI / 180.0;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int i = 0; i <= segs; i++)
            {
                double a = a0 + (sweepDeg * Math.PI / 180.0) * i / segs;
                var p = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r);
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, false);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}

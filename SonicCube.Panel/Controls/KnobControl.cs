using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 参数旋钮(自绘,Fluent 2 风格)。拖拽步进 = 0.01×(max-min),取整到 step;滚轮 ±step;
/// Alt+单击或双击回默认值;点击数值区域可键盘输入(clamp 到 min/max)。
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
        Width = 76; Height = 100;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, "Knob");
        IsEnabledChanged += (_, _) => InvalidateVisual();
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        LostKeyboardFocus += (_, _) => InvalidateVisual();
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
        nameof(ParamName), typeof(string), typeof(KnobControl), new PropertyMetadata("", (d, e) =>
        {
            var k = (KnobControl)d;
            string name = e.NewValue as string ?? "";
            AutomationProperties.SetName(k, string.IsNullOrEmpty(name) ? "Knob" : name);
            k.InvalidateVisual();
        }));
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
        if (e.ClickCount == 2) { ResetToDefault(); e.Handled = true; return; }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) { ResetToDefault(); e.Handled = true; return; }
        // 命中数值区域→进入编辑
        var p = e.GetPosition(this);
        if (p.Y > ActualHeight - 22)
        {
            BeginEdit(e);
            return;
        }
        _dragging = true;
        CaptureMouse();
        _dragStartY = p.Y;
        _dragStartValue = Value;
        InvalidateVisual();
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
        InvalidateVisual();
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
            var pos = TranslatePoint(new Point(4, ActualHeight - 20), panel);
            if (panel is Grid grid)
            {
                int row = Grid.GetRow(this), column = Grid.GetColumn(this);
                Grid.SetRow(_editor, row);
                Grid.SetColumn(_editor, column);
                Grid.SetRowSpan(_editor, Grid.GetRowSpan(this));
                Grid.SetColumnSpan(_editor, Grid.GetColumnSpan(this));

                // Grid positions each child within its cell; convert the parent
                // coordinates so the overlay cannot enlarge preceding Auto rows.
                for (int i = 0; i < row && i < grid.RowDefinitions.Count; i++)
                    pos.Y -= grid.RowDefinitions[i].ActualHeight;
                for (int i = 0; i < column && i < grid.ColumnDefinitions.Count; i++)
                    pos.X -= grid.ColumnDefinitions[i].ActualWidth;
            }
            _editor.MinWidth = 0;
            _editor.MinHeight = 0;
            _editor.Width = Math.Max(36, ActualWidth - 8);
            _editor.Height = 20;
            _editor.FontSize = 11;
            _editor.Padding = new Thickness(2, 0, 2, 0);
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

    // ── 缓存资源 ──
    private Brush? _panelDeep, _accent, _accentHover, _off, _textPrimary, _textSecondary, _textDisabled, _pillFill;
    private Brush? _capFill, _capFillHover;
    private Pen? _trackArcPen, _activeArcPen, _hoverArcPen, _capBorderPen, _pillBorderPen, _focusPen;
    private Pen? _pointerNormalPen, _pointerActivePen, _pointerDisabledPen;
    private Typeface? _labelTypeface, _valueTypeface;

    private void EnsureResources()
    {
        if (_capFill is not null) return;
        _panelDeep = this.GetDrawingBrush("BrushPanelDeep");
        _accent = this.GetDrawingBrush("BrushAccent");
        _accentHover = this.GetDrawingBrush("BrushAccentHover");
        _off = this.GetDrawingBrush("BrushOff");
        _textPrimary = this.GetDrawingBrush("BrushText");
        _textSecondary = this.GetDrawingBrush("BrushTextSecondary");
        _textDisabled = this.GetDrawingBrush("BrushTextDisabled");

        _pillFill = _panelDeep;
        _pillBorderPen = new Pen(this.GetDrawingBrush("BrushBorderLight"), 1);
        _pillBorderPen.Freeze();

        _capFill = new SolidColorBrush((Color)FindResource("ColorThumbBottom"));
        _capFill.Freeze();

        _capFillHover = new SolidColorBrush((Color)FindResource("ColorThumbTop"));
        _capFillHover.Freeze();

        _capBorderPen = new Pen(this.GetDrawingBrush("BrushBorderLight"), 1);
        _capBorderPen.Freeze();
        _focusPen = new Pen(_accent, 1.5);
        _focusPen.Freeze();

        _trackArcPen = new Pen(_off, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _activeArcPen = new Pen(_accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _hoverArcPen = new Pen(_accentHover, 3.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _trackArcPen.Freeze(); _activeArcPen.Freeze(); _hoverArcPen.Freeze();

        _pointerNormalPen = new Pen(_textPrimary, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pointerActivePen = new Pen(_accentHover, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pointerDisabledPen = new Pen(_textDisabled, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        _pointerNormalPen.Freeze(); _pointerActivePen.Freeze(); _pointerDisabledPen.Freeze();

        _labelTypeface = new Typeface((FontFamily)FindResource("FontPrimary"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _valueTypeface = new Typeface((FontFamily)FindResource("FontMono"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        EnsureResources();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        bool active = _dragging || IsMouseOver;
        double nameH = 15, valueH = 20;
        double knobH = Math.Max(20, h - nameH - valueH);
        double cx = w / 2, cy = nameH + knobH / 2;
        double outerRadius = Math.Min(w, knobH) / 2 - 2;
        double arcRadius = outerRadius - 2;
        double capRadius = Math.Max(6, arcRadius - 5.5);

        // 1. 顶部参数名称
        if (!string.IsNullOrEmpty(ParamName))
            DrawText(dc, ParamName, new Rect(0, 0, w, nameH), IsEnabled ? _textSecondary! : _textDisabled!, _labelTypeface!, 10.5);

        // 2. 底座深色阴影环
        dc.DrawEllipse(_panelDeep, null, new Point(cx, cy), capRadius + 2, capRadius + 2);

        // 3. 270° 轨道弧 + 值弧 (圆头 PenLineCap.Round)
        double t = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        t = Math.Clamp(t, 0, 1);

        DrawArc(dc, cx, cy, arcRadius, 135, 270, _trackArcPen!);
        if (t > 0.003 && IsEnabled)
            DrawArc(dc, cx, cy, arcRadius, 135, 270 * t, active ? _hoverArcPen! : _activeArcPen!);

        // 4. 中性旋钮面，悬停提亮；键盘焦点使用独立强调色轮廓。
        dc.DrawEllipse(active && IsEnabled ? _capFillHover : _capFill, _capBorderPen, new Point(cx, cy), capRadius, capRadius);
        if (IsKeyboardFocused && IsEnabled)
            dc.DrawEllipse(null, _focusPen, new Point(cx, cy), capRadius + 2, capRadius + 2);

        // 5. 清晰指针线
        double ang = (135 + 270 * t) * Math.PI / 180.0;
        var pOuter = new Point(cx + Math.Cos(ang) * (capRadius - 3), cy + Math.Sin(ang) * (capRadius - 3));
        var pInner = new Point(cx + Math.Cos(ang) * (capRadius * 0.28), cy + Math.Sin(ang) * (capRadius * 0.28));
        var pointerPen = !IsEnabled ? _pointerDisabledPen : (active ? _pointerActivePen : _pointerNormalPen);
        dc.DrawLine(pointerPen, pInner, pOuter);

        // 6. 底部数值读数胶囊 (Readout Pill)
        string txt = Step >= 1 ? Value.ToString("0") : Value.ToString("0.##");
        double pillW = Math.Min(w - 6, Math.Max(38, txt.Length * 7.5 + 14));
        var pillRect = new Rect((w - pillW) / 2, h - valueH + 1, pillW, valueH - 2);
        dc.DrawRoundedRectangle(_pillFill, _pillBorderPen, pillRect, 3.5, 3.5);
        DrawText(dc, txt, pillRect, IsEnabled ? _textPrimary! : _textDisabled!, _valueTypeface!, 11);
    }

    private void DrawText(DrawingContext dc, string text, Rect area, Brush brush, Typeface typeface, double size)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        ft.MaxTextWidth = Math.Max(4, area.Width);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(ft, new Point(area.X + (area.Width - ft.Width) / 2, area.Y + (area.Height - ft.Height) / 2));
    }

    private static void DrawArc(DrawingContext dc, double cx, double cy, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (r <= 1) return;
        int segs = Math.Max(2, (int)(Math.Abs(sweepDeg) / 5));
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

namespace SonicCube.Panel.Controls;

/// <summary>
/// 参数旋钮(见 ui_controls_report:拖拽步进=0.01×(max-min),取整;Alt/双击回默认;可手输 clamp)。
/// 用于效果参数(直发协议原值,无额外换算)。
/// </summary>
public sealed class Knob : Control
{
    private float _value, _min, _max = 100, _default, _step = 1;
    private bool _dragging;
    private int _dragStartY;
    private float _dragStartValue;
    private string _paramName = "";

    public event Action<float>? ValueCommitted;
    /// <summary>拖动中即时值(用于标签刷新)。</summary>
    public event Action<float>? ValueChanging;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string ParamName { get => _paramName; set { _paramName = value; Invalidate(); } }

    public Knob()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(72, 92);
    }

    public void Configure(float min, float max, float step, float def, string name)
    {
        _min = min; _max = max; _step = step; _default = def; _paramName = name;
        _value = Math.Clamp(def, min, max);
        Invalidate();
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _value;
        set { var v = Math.Clamp(value, _min, _max); if (Math.Abs(_value - v) < 1e-6) return; _value = v; Invalidate(); }
    }

    public void SetValueSilently(float v) { v = Math.Clamp(v, _min, _max); if (Math.Abs(_value - v) < 1e-6) return; _value = v; Invalidate(); }

    private float Snap(float v)
    {
        // 取整到步进(原 UI:拖拽步进后取整)。
        if (_step >= 1) return (float)Math.Round(v);
        return (float)(Math.Round(v / _step) * _step);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true; Capture = true;
            _dragStartY = e.Y; _dragStartValue = _value;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        float range = _max - _min;
        float delta = (_dragStartY - e.Y) * 0.01f * range; // 拖拽步进 = 0.01×(max-min)
        float nv = Snap(Math.Clamp(_dragStartValue + delta, _min, _max));
        if (Math.Abs(nv - _value) < 1e-6) return;
        _value = nv; Invalidate();
        ValueChanging?.Invoke(nv);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging)
        {
            _dragging = false; Capture = false;
            ValueCommitted?.Invoke(_value);
        }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) { _value = _default; Invalidate(); ValueCommitted?.Invoke(_value); }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        float nv = Snap(Math.Clamp(_value + (e.Delta > 0 ? _step : -_step), _min, _max));
        if (Math.Abs(nv - _value) < 1e-6) return;
        _value = nv; Invalidate();
        ValueCommitted?.Invoke(nv);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? BackColor);
        int cx = Width / 2, cy = 34, radius = 24;
        // 弧背景(270°)
        using (var arcPen = new Pen(Color.FromArgb(70, 70, 78), 4))
            g.DrawArc(arcPen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 270);
        // 值弧
        float t = (_max > _min) ? (_value - _min) / (_max - _min) : 0;
        using (var valPen = new Pen(Color.FromArgb(0, 150, 230), 4))
            g.DrawArc(valPen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 270 * t);
        // 指针
        double ang = (135 + 270 * t) * Math.PI / 180.0;
        var px = cx + (int)(Math.Cos(ang) * (radius - 6));
        var py = cy + (int)(Math.Sin(ang) * (radius - 6));
        using (var ptrPen = new Pen(Color.White, 2.5f))
            g.DrawLine(ptrPen, cx, cy, px, py);
        using (var dot = new SolidBrush(Color.FromArgb(50, 50, 56)))
            g.FillEllipse(dot, cx - 5, cy - 5, 10, 10);
        // 名称与值
        using var textBrush = new SolidBrush(ForeColor);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        using var nameFont = new Font(Font.FontFamily, 7.5f);
        g.DrawString(_paramName, nameFont, textBrush, new RectangleF(0, 60, Width, 14), fmt);
        g.DrawString(FormatValue(_value), Font, textBrush, new RectangleF(0, 74, Width, 16), fmt);
    }

    private string FormatValue(float v) => _step >= 1 ? v.ToString("0") : v.ToString("0.##");
}

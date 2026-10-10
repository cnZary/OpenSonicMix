namespace SonicCube.Panel.Controls;

/// <summary>线性垂直滑条(min~max 线性,双击回默认)。用于 Global EQ 总音量等线性参数。</summary>
public sealed class LinearSlider : Control
{
    private float _value, _min, _max = 100, _default;
    private bool _dragging;

    public event Action<float>? ValueCommitted;
    public event Action<float>? ValueChanging;

    public LinearSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Width = 50; Height = 180;
    }

    public void Configure(float min, float max, float def)
    {
        _min = min; _max = max; _default = def;
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

    private double TrackTop => 6;
    private double TrackHeight => Height - 32;

    private float YToValue(int y)
    {
        double t = 1 - (y - TrackTop) / TrackHeight;
        return (float)(_min + Math.Clamp(t, 0, 1) * (_max - _min));
    }

    private float ValueToY(float v)
    {
        double t = (v - _min) / (_max - _min);
        return (float)(TrackTop + (1 - t) * TrackHeight);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragging = true; Capture = true; Apply(e.Y); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) Apply(e.Y);
    }

    private void Apply(int y)
    {
        float nv = (float)Math.Round(YToValue(y));
        if (Math.Abs(nv - _value) < 0.5f) return;
        _value = nv; Invalidate();
        ValueChanging?.Invoke(nv);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragging) { _dragging = false; Capture = false; ValueCommitted?.Invoke(_value); }
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) { _value = _default; Invalidate(); ValueCommitted?.Invoke(_value); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        var trackRect = new RectangleF(Width / 2f - 3, (float)TrackTop, 6, (float)TrackHeight);
        using (var b = new SolidBrush(Color.FromArgb(60, 60, 66))) g.FillRectangle(b, trackRect);
        float y = ValueToY(_value);
        var thumb = new RectangleF(Width / 2f - 15, y - 6, 30, 12);
        using (var tb = new SolidBrush(Color.FromArgb(0, 122, 204))) g.FillRoundedRectangle(tb, thumb, 3);
        using var textBrush = new SolidBrush(ForeColor);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString(_value.ToString("0"), Font, textBrush, new RectangleF(0, Height - 22, Width, 18), fmt);
    }
}

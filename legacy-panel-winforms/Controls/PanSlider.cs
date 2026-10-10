namespace SonicCube.Panel.Controls;

/// <summary>
/// 声像 L-R 水平推子(见 ui_controls_report:float -50~50 直发;双击复位 0;mono 开启时可用)。
/// </summary>
public sealed class PanSlider : Control
{
    private float _value; // -50~50
    private bool _dragging;

    public event Action<float>? ValueCommitted;
    public event Action<float>? ValueChanging;

    public PanSlider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(120, 26);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Value
    {
        get => _value;
        set { var v = Math.Clamp(value, -50, 50); if (Math.Abs(_value - v) < 1e-6) return; _value = v; Invalidate(); }
    }

    public void SetValueSilently(float v) { v = Math.Clamp(v, -50, 50); if (Math.Abs(_value - v) < 1e-6) return; _value = v; Invalidate(); }

    private float XToValue(int x)
    {
        double t = Math.Clamp((x - 6.0) / (Width - 12.0), 0, 1);
        return (float)(t * 100 - 50);
    }

    private float ValueToX(float v) => 6 + (float)((v + 50) / 100.0 * (Width - 12));

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _dragging = true; Capture = true; ApplyX(e.X); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ApplyX(e.X);
    }

    private void ApplyX(int x)
    {
        float nv = (float)Math.Round(XToValue(x));
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
        if (e.Button == MouseButtons.Left) { _value = 0; Invalidate(); ValueCommitted?.Invoke(0); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        int cy = Height / 2;
        using (var trackPen = new Pen(Color.FromArgb(70, 70, 78), 3))
            g.DrawLine(trackPen, 6, cy, Width - 6, cy);
        // 中线
        using (var midPen = new Pen(Color.FromArgb(100, 100, 108), 1))
            g.DrawLine(midPen, Width / 2, cy - 6, Width / 2, cy + 6);
        float x = ValueToX(_value);
        var thumb = new RectangleF(x - 7, cy - 8, 14, 16);
        using (var b = new SolidBrush(Enabled ? Color.FromArgb(0, 122, 204) : Color.FromArgb(80, 80, 86)))
            g.FillRoundedRectangle(b, thumb, 3);
        using var textBrush = new SolidBrush(ForeColor);
        var fmt = new StringFormat { Alignment = StringAlignment.Center };
        string txt = _value == 0 ? "C" : (_value < 0 ? $"L{-_value:0}" : $"R{_value:0}");
        g.DrawString(txt, new Font(Font.FontFamily, 7f), textBrush, new RectangleF(0, 0, Width, 12), fmt);
    }
}

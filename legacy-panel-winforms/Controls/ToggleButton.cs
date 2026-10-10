namespace SonicCube.Panel.Controls;

/// <summary>带点亮状态的切换按钮(不发协议,仅 UI + 事件)。</summary>
public sealed class ToggleButton : Control
{
    private bool _checked;
    private bool _hover;

    public event Action<bool>? CheckedChanged;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? ActiveText { get; set; }

    public ToggleButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Size = new Size(64, 28);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
        }
    }

    public void SetCheckedSilently(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
        CheckedChanged?.Invoke(Checked);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Color fill = _checked
            ? Color.FromArgb(0, 122, 204)
            : (_hover ? Color.FromArgb(70, 70, 78) : Color.FromArgb(52, 52, 58));
        Color border = _checked ? Color.FromArgb(40, 160, 240) : Color.FromArgb(80, 80, 88);
        using (var b = new SolidBrush(fill)) g.FillRoundedRectangle(b, r, 5);
        using (var p = new Pen(border)) g.DrawRoundedRectangle(p, r, 5);
        using var textBrush = new SolidBrush(_checked ? Color.White : Color.FromArgb(200, 200, 205));
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), fmt);
    }
}

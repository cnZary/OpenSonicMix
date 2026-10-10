using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 立体声电平表(见 ui_controls_report 附录电平换算)。
/// dB≤-60 → 空;dB≥0 → 满(红);否则 |dB|/60。限频刷新(默认 25 FPS),仅保留最新值。
/// </summary>
public sealed class StereoMeter : Control
{
    private float _leftDb = -100, _rightDb = -100;
    private float _pendingL = -100, _pendingR = -100;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private bool _dirty;

    public StereoMeter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        Width = 40; Height = 160;
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 40 }; // ~25 FPS
        _refreshTimer.Tick += (_, _) =>
        {
            if (_dirty)
            {
                _dirty = false;
                _leftDb = _pendingL; _rightDb = _pendingR;
                Invalidate();
            }
        };
        _refreshTimer.Start();
    }

    /// <summary>提交最新电平(仅保留最新值,防积压)。</summary>
    public void SetLevel(float leftDb, float rightDb)
    {
        _pendingL = leftDb; _pendingR = rightDb;
        _dirty = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        int barW = (Width - 8) / 2;
        DrawBar(g, new Rectangle(2, 4, barW, Height - 8), _leftDb);
        DrawBar(g, new Rectangle(4 + barW, 4, barW, Height - 8), _rightDb);
    }

    private void DrawBar(Graphics g, Rectangle r, float db)
    {
        using (var bg = new SolidBrush(Color.FromArgb(40, 40, 44)))
            g.FillRectangle(bg, r);
        double prog = ValueMapping.MeterDbToProgress(db); // 1=空, 0=满
        double fill = 1.0 - prog;                        // 0=空, 1=满
        int fillH = (int)(r.Height * fill);
        if (fillH > 0)
        {
            var fr = new Rectangle(r.X, r.Bottom - fillH, r.Width, fillH);
            // 绿→黄→红 渐变
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                r, Color.FromArgb(220, 60, 60), Color.FromArgb(60, 200, 90),
                System.Drawing.Drawing2D.LinearGradientMode.Vertical);
            var blend = new System.Drawing.Drawing2D.ColorBlend(3)
            {
                Colors = new[] { Color.FromArgb(60, 200, 90), Color.FromArgb(220, 200, 60), Color.FromArgb(220, 60, 60) },
                Positions = new[] { 0f, 0.75f, 1f },
            };
            brush.InterpolationColors = blend;
            g.FillRectangle(brush, fr);
        }
        using var border = new Pen(Color.FromArgb(70, 70, 76));
        g.DrawRectangle(border, r);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}

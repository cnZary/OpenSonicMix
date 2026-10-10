using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// Global EQ(GRAPHEQ,模块 1)三拖拽点面板(见 ui_controls_report 4.3)。
/// 点 0=低切(freq+gain),点 1=峰值(freq+gain+Q 滚轮),点 2=高切(freq+gain)。
/// freq 对数轴 20~20000Hz,gain ±20dB。拖动中不发送,松手才发送(与原版一致)。
/// </summary>
public sealed class GlobalEqPanel : Control
{
    public sealed class EqPoint
    {
        public double Freq { get; set; }   // Hz
        public double Gain { get; set; }   // dB
        public double Q { get; set; } = 0.7;
    }

    private readonly EqPoint[] _points =
    {
        new EqPoint { Freq = 100, Gain = 0 },
        new EqPoint { Freq = 1000, Gain = 0, Q = 0.7 },
        new EqPoint { Freq = 5000, Gain = 0 },
    };

    private int _dragIndex = -1;

    /// <summary>松手时提交参数。location: 0/1(低切 freq/gain), 2/3/4(峰 freq/Q/gain), 5/6(高切 freq/gain)。</summary>
    public event Action<int /*location*/, float /*value*/>? ParameterCommitted;

    public GlobalEqPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Height = 180;
    }

    public double GetFreq(int i) => _points[i].Freq;
    public double GetGain(int i) => _points[i].Gain;
    public double GetQ() => _points[1].Q;

    public void SetPoint(int i, double freq, double gain)
    {
        _points[i].Freq = Math.Clamp(freq, 20, 20000);
        _points[i].Gain = Math.Clamp(gain, -20, 20);
        Invalidate();
    }

    public void SetQ(double q) { _points[1].Q = Math.Clamp(q, 0.1, 10); Invalidate(); }

    private double PointX(int i) => ValueMapping.EqFreqToNorm(_points[i].Freq) * Width;
    private double PointY(int i) => ValueMapping.EqGainToNorm(_points[i].Gain) * Height;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            for (int i = 0; i < 3; i++)
            {
                double dx = e.X - PointX(i), dy = e.Y - PointY(i);
                if (dx * dx + dy * dy < 14 * 14) { _dragIndex = i; Capture = true; break; }
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0) return;
        double freq = ValueMapping.EqPixelToFreq(e.X, Width);
        double gain = ValueMapping.EqPixelToGain(e.Y, Height);
        _points[_dragIndex].Freq = freq;
        _points[_dragIndex].Gain = gain;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragIndex >= 0)
        {
            int i = _dragIndex;
            _dragIndex = -1; Capture = false;
            Commit(i);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        // 仅峰点 Q(滚轮/手势),立即发送。
        double nq = Math.Clamp(_points[1].Q + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 10);
        if (Math.Abs(nq - _points[1].Q) < 1e-6) return;
        _points[1].Q = nq;
        Invalidate();
        ParameterCommitted?.Invoke(3, (float)nq);
    }

    private void Commit(int i)
    {
        var p = _points[i];
        switch (i)
        {
            case 0:
                ParameterCommitted?.Invoke(0, (float)p.Freq);
                ParameterCommitted?.Invoke(1, (float)p.Gain);
                break;
            case 1:
                ParameterCommitted?.Invoke(2, (float)p.Freq);
                ParameterCommitted?.Invoke(4, (float)p.Gain);
                break;
            case 2:
                ParameterCommitted?.Invoke(5, (float)p.Freq);
                ParameterCommitted?.Invoke(6, (float)p.Gain);
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(BackColor);
        // 网格
        using var gridPen = new Pen(Color.FromArgb(55, 55, 62));
        foreach (double f in new[] { 100.0, 1000, 10000 })
        {
            float x = (float)(ValueMapping.EqFreqToNorm(f) * Width);
            g.DrawLine(gridPen, x, 0, x, Height);
        }
        g.DrawLine(gridPen, 0, Height / 2, Width, Height / 2);
        // 响应曲线(三段抛物线叠加近似)
        DrawCurve(g);
        // 拖拽点
        var colors = new[] { Color.FromArgb(90, 200, 120), Color.FromArgb(240, 180, 70), Color.FromArgb(90, 160, 240) };
        for (int i = 0; i < 3; i++)
        {
            float x = (float)PointX(i), y = (float)PointY(i);
            using var b = new SolidBrush(colors[i]);
            g.FillEllipse(b, x - 7, y - 7, 14, 14);
            using var p = new Pen(Color.White, 1.5f);
            g.DrawEllipse(p, x - 7, y - 7, 14, 14);
        }
    }

    private void DrawCurve(Graphics g)
    {
        using var pen = new Pen(Color.FromArgb(0, 180, 230), 2);
        PointF prev = new(0, Height / 2);
        for (int x = 0; x <= Width; x += 4)
        {
            double freq = ValueMapping.EqPixelToFreq(x, Width);
            double total = 0;
            foreach (var p in _points)
                total += p.Gain * Math.Exp(-Math.Pow(Math.Log(freq / p.Freq, 2), 2) * (i_sharpness(p)));
            double y = ValueMapping.EqGainToNorm(Math.Clamp(total, -20, 20)) * Height;
            var cur = new PointF(x, (float)y);
            g.DrawLine(pen, prev, cur);
            prev = cur;
        }
    }

    private double i_sharpness(EqPoint p) => 6.0 / Math.Max(0.2, p.Q);
}

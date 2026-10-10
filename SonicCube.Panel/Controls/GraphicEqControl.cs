using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// Global EQ(GRAPHEQ,模块 1)三拖拽点图示面板。
/// 点 0=低切(freq+gain),点 1=峰值(freq+gain,滚轮调 Q),点 2=高切(freq+gain)。
/// freq 对数轴 20~20000Hz,gain ±20dB。拖动中不发送,松手才提交(与官方一致)。
/// </summary>
public sealed class GraphicEqControl : FrameworkElement
{
    public GraphicEqControl()
    {
        SnapsToDevicePixels = true;
        MinHeight = 140;
    }

    // location: 0/1(低切 freq/gain), 2/3/4(峰 freq/Q/gain), 5/6(高切 freq/gain)
    public event Action<int, float>? ParameterCommitted;

    private readonly double[] _freq = { 100, 1000, 5000 };
    private readonly double[] _gain = { 0, 0, 0 };
    private double _q = 0.7;
    private int _dragIndex = -1;

    public void SetPoint(int i, double freq, double gain)
    {
        _freq[i] = Math.Clamp(freq, 20, 20000);
        _gain[i] = Math.Clamp(gain, -20, 20);
        InvalidateVisual();
    }

    public void SetQ(double q) { _q = Math.Clamp(q, 0.1, 10); InvalidateVisual(); }

    private double PointX(int i) => ValueMapping.EqFreqToNorm(_freq[i]) * ActualWidth;
    private double PointY(int i) => ValueMapping.EqGainToNorm(_gain[i]) * ActualHeight;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        for (int i = 0; i < 3; i++)
        {
            double dx = p.X - PointX(i), dy = p.Y - PointY(i);
            if (dx * dx + dy * dy < 15 * 15)
            {
                _dragIndex = i;
                CaptureMouse();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragIndex < 0) return;
        var p = e.GetPosition(this);
        _freq[_dragIndex] = ValueMapping.EqPixelToFreq(p.X, ActualWidth);
        _gain[_dragIndex] = ValueMapping.EqPixelToGain(p.Y, ActualHeight);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        int i = _dragIndex;
        _dragIndex = -1;
        ReleaseMouseCapture();
        Commit(i);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        // 峰点 Q 立即发送
        double nq = Math.Clamp(_q + (e.Delta > 0 ? 0.05 : -0.05), 0.1, 10);
        if (Math.Abs(nq - _q) < 1e-6) return;
        _q = nq;
        InvalidateVisual();
        ParameterCommitted?.Invoke(3, (float)nq);
        e.Handled = true;
    }

    private void Commit(int i)
    {
        switch (i)
        {
            case 0:
                ParameterCommitted?.Invoke(0, (float)_freq[0]);
                ParameterCommitted?.Invoke(1, (float)_gain[0]);
                break;
            case 1:
                ParameterCommitted?.Invoke(2, (float)_freq[1]);
                ParameterCommitted?.Invoke(4, (float)_gain[1]);
                break;
            case 2:
                ParameterCommitted?.Invoke(5, (float)_freq[2]);
                ParameterCommitted?.Invoke(6, (float)_gain[2]);
                break;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 8) return;
        dc.DrawRectangle((Brush)FindResource("BrushPanelDeep"), null, new Rect(0, 0, w, h));

        // 网格
        var gridPen = new Pen((Brush)FindResource("BrushBorder"), 1);
        foreach (double f in new[] { 100.0, 1000, 10000 })
        {
            double x = ValueMapping.EqFreqToNorm(f) * w;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        }
        dc.DrawLine(gridPen, new Point(0, h / 2), new Point(w, h / 2));

        // 响应曲线(三段高斯叠加近似,仅视觉)
        var curvePen = new Pen((Brush)FindResource("BrushAccent"), 2);
        Point prev = new(0, h / 2);
        for (double x = 0; x <= w; x += 3)
        {
            double freq = ValueMapping.EqPixelToFreq(x, w);
            double total = 0;
            for (int i = 0; i < 3; i++)
            {
                double sharp = 6.0 / Math.Max(0.2, i == 1 ? _q : 0.9);
                total += _gain[i] * Math.Exp(-Math.Pow(Math.Log(freq / _freq[i], 2), 2) * sharp);
            }
            double y = ValueMapping.EqGainToNorm(Math.Clamp(total, -20, 20)) * h;
            var cur = new Point(x, y);
            dc.DrawLine(curvePen, prev, cur);
            prev = cur;
        }

        // 拖拽点
        var colors = new[] { (Color)FindResource("ColorOn"), (Color)FindResource("ColorWarn"), (Color)FindResource("ColorAccent") };
        for (int i = 0; i < 3; i++)
        {
            double x = PointX(i), y = PointY(i);
            dc.DrawEllipse(new SolidColorBrush(colors[i]), new Pen(Brushes.White, 1.2), new Point(x, y), 7, 7);
            string txt = $"{FormatFreq(_freq[i])} {_gain[i]:+0;-0;0}dB" + (i == 1 ? $" Q{_q:0.##}" : "");
            DrawText(dc, txt, new Point(x, y - 20));
        }
        AutomationProperties.SetName(this, "Global EQ");
    }

    private static string FormatFreq(double f) => f >= 1000 ? $"{f / 1000:0.##}k" : $"{f:0}";

    private void DrawText(DrawingContext dc, string text, Point center)
    {
        var brush = (Brush)FindResource("BrushText");
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 9, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }
}

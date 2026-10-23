using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// Global EQ(GRAPHEQ,模块 1)三拖拽点图示面板(Fluent 2 风格)。
/// 点 0=低切(freq+gain),点 1=峰值(freq+gain,滚轮调 Q),点 2=高切(freq+gain)。
/// freq 对数轴 20~20000Hz,gain ±20dB。拖动中不发送,松手才提交(与官方一致)。
/// </summary>
public sealed class GraphicEqControl : FrameworkElement
{
    public GraphicEqControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        MinHeight = 148;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, "Global EQ");
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CancelDrag(); InvalidateVisual(); };
        Unloaded += (_, _) => CancelDrag();
    }

    // location: 0/1(低切 freq/gain), 2/3/4(峰 freq/Q/gain), 5/6(高切 freq/gain)
    public event Action<int, float>? ParameterCommitted;

    private readonly double[] _freq = { 100, 1000, 5000 };
    private readonly double[] _gain = { 0, 0, 0 };
    private readonly double[] _synchronizedFreq = { 100, 1000, 5000 };
    private readonly double[] _synchronizedGain = { 0, 0, 0 };
    private double _q = 0.7;
    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    public void SetPoint(int i, double freq, double gain)
    {
        freq = Math.Clamp(freq, 20, 20000);
        gain = Math.Clamp(gain, -20, 20);
        _synchronizedFreq[i] = freq;
        _synchronizedGain[i] = gain;
        if (_dragIndex == i) return;
        if (_freq[i] == freq && _gain[i] == gain) return;
        _freq[i] = freq;
        _gain[i] = gain;
        _curveDirty = true;
        InvalidateVisual();
    }

    public void SetQ(double q)
    {
        q = Math.Clamp(q, 0.1, 10);
        if (_q == q) return;
        _q = q;
        _curveDirty = true;
        InvalidateVisual();
    }

    private void CancelDrag()
    {
        int i = _dragIndex;
        _dragIndex = _hoverIndex = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (i >= 0) SetPoint(i, _synchronizedFreq[i], _synchronizedGain[i]);
        Cursor = Cursors.Cross;
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragIndex >= 0) CancelDrag();
    }

    private double PointX(int i) => ValueMapping.EqFreqToNorm(_freq[i]) * ActualWidth;
    private double PointY(int i) => ValueMapping.EqGainToNorm(_gain[i]) * ActualHeight;

    // ── 画刷/画笔缓存 ──
    private Brush? _bgBrush, _axisTextBrush, _badgeBgBrush, _textBrush;
    private LinearGradientBrush? _curveAreaFill;
    private Pen? _gridPen, _zeroPen, _curvePen, _pointPen, _badgeBorderPen;
    private Brush[]? _pointBrushes;
    private Brush[]? _haloBrushes;
    private Typeface? _typeface;

    private void EnsureResources()
    {
        if (_pointBrushes is not null) return;
        _bgBrush = this.GetDrawingBrush("BrushMeterTrack");
        _axisTextBrush = this.GetDrawingBrush("BrushTextTertiary");
        _textBrush = this.GetDrawingBrush("BrushText");
        var accent = ((SolidColorBrush)FindResource("BrushAccent")).Color;

        _curveAreaFill = new LinearGradientBrush(
            Color.FromArgb(0x28, accent.R, accent.G, accent.B),
            Color.FromArgb(0x06, accent.R, accent.G, accent.B), 90);
        _curveAreaFill.Freeze();

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        gridBrush.Freeze();
        _gridPen = new Pen(gridBrush, 1);
        _gridPen.Freeze();

        var zeroBrush = new SolidColorBrush(Color.FromArgb(0x32, 0xFF, 0xFF, 0xFF));
        zeroBrush.Freeze();
        _zeroPen = new Pen(zeroBrush, 1);
        _zeroPen.Freeze();

        _curvePen = new Pen(this.GetDrawingBrush("BrushAccent"), 2.2)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        _curvePen.Freeze();

        _pointPen = new Pen(_textBrush, 1.5);
        _pointPen.Freeze();

        _badgeBgBrush = this.GetDrawingBrush("BrushPanelDeep");
        var badgeBorder = this.GetDrawingBrush("BrushBorderLight");
        _badgeBorderPen = new Pen(badgeBorder, 1);
        _badgeBorderPen.Freeze();

        Color c0 = (Color)FindResource("ColorOn");
        Color c1 = (Color)FindResource("ColorAmber");
        Color c2 = accent;
        _pointBrushes =
        [
            new SolidColorBrush(c0),
            new SolidColorBrush(c1),
            new SolidColorBrush(c2),
        ];
        _haloBrushes =
        [
            new SolidColorBrush(Color.FromArgb(0x38, c0.R, c0.G, c0.B)),
            new SolidColorBrush(Color.FromArgb(0x38, c1.R, c1.G, c1.B)),
            new SolidColorBrush(Color.FromArgb(0x38, c2.R, c2.G, c2.B)),
        ];
        foreach (var b in _pointBrushes) b.Freeze();
        foreach (var b in _haloBrushes) b.Freeze();

        _typeface = new Typeface((FontFamily)FindResource("FontMono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var p = e.GetPosition(this);
        for (int i = 0; i < 3; i++)
        {
            double dx = p.X - PointX(i), dy = p.Y - PointY(i);
            if (dx * dx + dy * dy < 16 * 16)
            {
                _dragIndex = i;
                CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_dragIndex < 0)
        {
            int hit = -1;
            for (int i = 0; i < 3; i++)
            {
                double dx = p.X - PointX(i), dy = p.Y - PointY(i);
                if (dx * dx + dy * dy < 16 * 16) { hit = i; break; }
            }
            if (hit != _hoverIndex)
            {
                _hoverIndex = hit;
                Cursor = hit >= 0 ? Cursors.Hand : Cursors.Cross;
                InvalidateVisual();
            }
            return;
        }
        _freq[_dragIndex] = ValueMapping.EqPixelToFreq(p.X, ActualWidth);
        _gain[_dragIndex] = ValueMapping.EqPixelToGain(p.Y, ActualHeight);
        _curveDirty = true;
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; InvalidateVisual(); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        int i = _dragIndex;
        _dragIndex = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
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
        _curveDirty = true;
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

    private static readonly (double Freq, string Label)[] GridFreqs =
    [
        (50, "50"), (100, "100"), (200, "200"), (500, "500"),
        (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k")
    ];

    private double _cachedW, _cachedH, _cachedDpi;
    private DrawingGroup? _staticBackdrop;

    private void EnsureStaticCache(double w, double h, double dpi)
    {
        if (_staticBackdrop is not null
            && Math.Abs(w - _cachedW) < 0.05
            && Math.Abs(h - _cachedH) < 0.05
            && Math.Abs(dpi - _cachedDpi) < 0.001)
            return;

        _cachedW = w;
        _cachedH = h;
        _cachedDpi = dpi;

        double midY = Math.Round(h / 2) + 0.5;
        var backdrop = new DrawingGroup();
        using (var dc = backdrop.Open())
        {
            dc.DrawRectangle(_bgBrush, null, new Rect(0, 0, w, h));

            foreach (double g in new[] { 10.0, -10.0 })
            {
                double gy = Math.Round(ValueMapping.EqGainToNorm(g) * h) + 0.5;
                dc.DrawLine(_gridPen, new Point(0, gy), new Point(w, gy));
            }
            dc.DrawLine(_zeroPen, new Point(0, midY), new Point(w, midY));

            foreach (var (f, lbl) in GridFreqs)
            {
                double x = Math.Round(ValueMapping.EqFreqToNorm(f) * w) + 0.5;
                dc.DrawLine(_gridPen, new Point(x, 0), new Point(x, h));
                if (f is 100 or 1000 or 10000)
                {
                    var ft = new FormattedText(lbl, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        _typeface!, 8.5, _axisTextBrush!, dpi);
                    dc.DrawText(ft, new Point(x + 3, h - ft.Height - 2));
                }
            }
        }
        backdrop.Freeze();
        _staticBackdrop = backdrop;
    }

    private bool _curveDirty = true;
    private double _curveW, _curveH;
    private StreamGeometry? _strokeGeometry, _fillGeometry;

    private void EnsureCurveCache(double w, double h)
    {
        if (!_curveDirty && _curveW == w && _curveH == h) return;
        double midY = Math.Round(h / 2) + 0.5;

        // 2. 响应曲线与半透明包络填充
        var strokeGeo = new StreamGeometry();
        var fillGeo = new StreamGeometry();
        using (var sCtx = strokeGeo.Open())
        using (var fCtx = fillGeo.Open())
        {
            int steps = Math.Max(16, (int)(w / 2.5));
            fCtx.BeginFigure(new Point(0, midY), true, true);
            for (int s = 0; s <= steps; s++)
            {
                double x = w * s / steps;
                double freq = ValueMapping.EqPixelToFreq(x, w);
                double total = 0;
                for (int i = 0; i < 3; i++)
                {
                    double sharp = 6.0 / Math.Max(0.2, i == 1 ? _q : 0.9);
                    total += _gain[i] * Math.Exp(-Math.Pow(Math.Log(freq / _freq[i], 2), 2) * sharp);
                }
                double y = ValueMapping.EqGainToNorm(Math.Clamp(total, -20, 20)) * h;
                var pt = new Point(x, y);
                if (s == 0) sCtx.BeginFigure(pt, false, false);
                else sCtx.LineTo(pt, true, true);
                fCtx.LineTo(pt, true, true);
            }
            fCtx.LineTo(new Point(w, midY), true, true);
        }
        strokeGeo.Freeze();
        fillGeo.Freeze();
        _strokeGeometry = strokeGeo;
        _fillGeometry = fillGeo;
        _curveW = w;
        _curveH = h;
        _curveDirty = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 8 || h < 8) return;
        EnsureResources();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureStaticCache(w, h, dpi);
        dc.DrawDrawing(_staticBackdrop);
        EnsureCurveCache(w, h);
        dc.DrawGeometry(_curveAreaFill, null, _fillGeometry);
        dc.DrawGeometry(null, _curvePen, _strokeGeometry);

        // 3. 三频段控制点，仅悬停/拖动时显示反馈环。
        for (int i = 0; i < 3; i++)
        {
            double x = PointX(i), y = PointY(i);
            bool active = i == _dragIndex || i == _hoverIndex;
            if (active)
                dc.DrawEllipse(_haloBrushes![i], null, new Point(x, y), 12, 12);
            dc.DrawEllipse(_pointBrushes![i], _pointPen, new Point(x, y), active ? 7 : 6, active ? 7 : 6);

            string txt = $"{FormatFreq(_freq[i])} {_gain[i]:+0;-0;0}dB" + (i == 1 ? $" Q{_q:0.##}" : "");
            DrawBadge(dc, txt, new Point(x, y < 28 ? y + 18 : y - 18), _textBrush!, dpi, w);
        }
    }

    private static string FormatFreq(double f) => f >= 1000 ? $"{f / 1000:0.##}k" : $"{f:0}";

    private void DrawBadge(DrawingContext dc, string text, Point center, Brush brush, double dpi, double maxW)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            _typeface!, 9, brush, dpi);
        double bw = ft.Width + 8, bh = ft.Height + 3;
        double bx = Math.Clamp(center.X - bw / 2, 2, Math.Max(2, maxW - bw - 2));
        double by = center.Y - bh / 2;
        dc.DrawRoundedRectangle(_badgeBgBrush, _badgeBorderPen, new Rect(bx, by, bw, bh), 3, 3);
        dc.DrawText(ft, new Point(bx + 4, by + 1.5));
    }
}

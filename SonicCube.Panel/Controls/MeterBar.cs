using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 双声道电平表(自绘,DrawingContext,Fluent 2 风格)。
/// 输入为协议 dB;内部以“满刻度比例 0..1”渲染(dB≤-60→0,dB≥0→1)。
/// 快攻慢放 + 峰值保持:按实际经过时间计算,不按帧数。
/// 所有实例统一由静态 CompositionTarget.Rendering 驱动(单一订阅,窗口关闭时解除)。
/// 最小化/不可见时暂停绘制。
/// 视觉:分段渐变电平条(绿→黄→红,分界处颜色连续)、右侧 dB 刻度尺(数字 + 刻度线)、双段式峰值保持线。
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    // ── 静态渲染驱动(全应用一个订阅)────────────────────────
    private static readonly List<WeakReference<MeterBar>> Active = new();
    private static bool _subscribed;
    private static DateTime _lastFrame = DateTime.MinValue;

    public MeterBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        _display = new double[2];
        _peak = new double[2];
        _peakHoldUntil = new DateTime[2];
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => Register();
        Unloaded += (_, _) => Unregister();
    }

    /// <summary>目标电平(dB,-100=无信号)。线程安全:仅保留最新。</summary>
    private volatile float _targetL = -100f;
    private volatile float _targetR = -100f;

    private readonly double[] _display;       // 渲染用平滑值(0..1)
    private readonly double[] _peak;          // 峰值保持(0..1)
    private readonly DateTime[] _peakHoldUntil;

    // 快攻慢放(dB/秒)
    private const double AttackDbPerSec = 240;   // 快速上升
    private const double ReleaseDbPerSec = 12;   // 缓慢回落
    private static readonly TimeSpan PeakHold = TimeSpan.FromMilliseconds(900);
    private const double PeakReleaseDbPerSec = 18;

    /// <summary>提交最新电平(dB)。任何线程可调用,只保留最新值,不向 Dispatcher 排队。</summary>
    public void SetLevelDb(float leftDb, float rightDb)
    {
        _targetL = leftDb;
        _targetR = rightDb;
    }

    private static double DbToNorm(double db)
    {
        // 进度 = |dB|/60 (dB≤-60→0 空,dB≥0→1 满)
        double progress = ValueMapping.MeterDbToProgress(db);
        return 1.0 - progress;
    }

    private void UpdateSubscription()
    {
        bool visible = IsVisible && ActualHeight > 0;
        if (visible) Register(); else Unregister();
    }

    private void Register()
    {
        lock (Active)
        {
            foreach (var wr in Active)
                if (wr.TryGetTarget(out var m) && ReferenceEquals(m, this)) return;
            Active.Add(new WeakReference<MeterBar>(this));
            if (_subscribed) return;
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    private void Unregister()
    {
        lock (Active)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (!Active[i].TryGetTarget(out var m) || ReferenceEquals(m, this))
                    Active.RemoveAt(i);
            }
            if (Active.Count == 0 && _subscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _subscribed = false;
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        double dt = _lastFrame == DateTime.MinValue ? 0.016 : (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        dt = Math.Clamp(dt, 0, 0.1);

        lock (Active)
        {
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (!Active[i].TryGetTarget(out var m)) { Active.RemoveAt(i); continue; }
                if (!m.IsVisible || m.ActualHeight <= 0) continue;
                m.Step(now, dt);
            }
            if (Active.Count == 0 && _subscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _subscribed = false;
            }
        }
    }

    private void Step(DateTime now, double dt)
    {
        double targetL = DbToNorm(_targetL);
        double targetR = DbToNorm(_targetR);
        bool changed = false;
        changed |= StepChannel(0, targetL, now, dt);
        changed |= StepChannel(1, targetR, now, dt);
        if (changed) InvalidateVisual();
    }

    private bool StepChannel(int ch, double target, DateTime now, double dt)
    {
        bool changed = false;
        double cur = _display[ch];
        double stepUp = AttackDbPerSec * dt / 60.0;
        double stepDown = ReleaseDbPerSec * dt / 60.0;
        double next;
        if (target > cur) next = Math.Min(target, cur + stepUp);
        else next = Math.Max(target, cur - stepDown);
        if (Math.Abs(next - cur) > 1e-4) { _display[ch] = next; changed = true; }

        // 峰值保持
        if (target >= _peak[ch])
        {
            _peak[ch] = target;
            _peakHoldUntil[ch] = now + PeakHold;
            changed = true;
        }
        else if (now > _peakHoldUntil[ch])
        {
            double pkDb = -60 + _peak[ch] * 60;
            double npDb = Math.Max(-60, pkDb - PeakReleaseDbPerSec * dt);
            double np = (npDb + 60) / 60;
            if (Math.Abs(np - _peak[ch]) > 1e-4) { _peak[ch] = np; changed = true; }
        }
        return changed;
    }

    // ── 画刷缓存(资源查找走 Application.Resources,固定引用,实例间共享)──
    private Brush? _track, _tick, _peakWhite, _peakWarn, _scaleText, _scaleTextHi;
    private Pen? _trackPen, _tickPen, _peakWhitePen, _peakWarnPen;
    private Typeface? _typeface;
    private LinearGradientBrush? _fill;      // 电平条渐变(绿→黄→红,分界连续)
    private DrawingBrush? _segments;         // 分段横纹(平铺在电平条上)

    private void EnsureBrushes()
    {
        if (_fill is not null) return;
        _track = (Brush)FindResource("BrushMeterTrack");
        _tick = (Brush)FindResource("BrushTickMajor");
        _peakWhite = (Brush)FindResource("BrushThumbLine");
        _peakWarn = (Brush)FindResource("BrushWarn");
        _scaleText = (Brush)FindResource("BrushTextDisabled");
        _scaleTextHi = (Brush)FindResource("BrushTextSecondary");

        var trackBorder = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        trackBorder.Freeze();
        _trackPen = new Pen(trackBorder, 1);
        _tickPen = new Pen(_tick, 1);
        _peakWhitePen = new Pen(_peakWhite, 1.2);
        _peakWarnPen = new Pen(_peakWarn, 1.4);
        _trackPen.Freeze(); _tickPen.Freeze(); _peakWhitePen.Freeze(); _peakWarnPen.Freeze();
        _typeface = new Typeface(new FontFamily("Cascadia Mono, Bahnschrift, Segoe UI"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);

        // 电平条渐变:坐标映射到整根轨道(0=底/-60dB,1=顶/0dB)。
        var green = (Color)FindResource("ColorMeterGreen");
        var yellow = (Color)FindResource("ColorMeterYellow");
        var red = (Color)FindResource("ColorMeterRed");
        const double yTop = 1 - 3.0 / 60;    // -3dB 归一
        const double yMid = 1 - 12.0 / 60;   // -12dB 归一
        var stops = new GradientStopCollection
        {
            new(Scale(green, 1.05), 0),
            new(Scale(green, 1.12), yMid - 0.035),
            new(Scale(yellow, 1.05), yMid + 0.035),
            new(Scale(yellow, 1.12), yTop - 0.035),
            new(Scale(red, 1.08), yTop + 0.035),
            new(Scale(red, 1.25), 1),
        };
        _fill = new LinearGradientBrush(stops, new Point(0, 1), new Point(0, 0));
        _fill.Freeze();

        // 分段横纹:0..3px 透明露出下层渐变色,3..4.5px 用轨道深色画横纹间隙。
        // 注意:DrawingBrush 的 Viewbox 默认按 BoundingBox 归一化,所以必须同时放置透明底框(0,0,1,4.5)
        // 或显式指定 Viewbox=(0,0,1,4.5)+Absolute,否则单个子矩形会被 Stretch.Fill 拉伸铺满整个 Viewport!
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 1, 4.5))));
        group.Children.Add(new GeometryDrawing(_track, null, new RectangleGeometry(new Rect(0, 3, 1, 1.5))));
        group.Freeze();
        _segments = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewbox = new Rect(0, 0, 1, 4.5),
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 1, 4.5),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.Fill,
        };
        _segments.Freeze();
    }

    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A,
            (byte)Math.Min(255, (int)(c.R * f + 0.5)),
            (byte)Math.Min(255, (int)(c.G * f + 0.5)),
            (byte)Math.Min(255, (int)(c.B * f + 0.5)));

    private static GuidelineSet VerticalGuidelines(params double[] xs) => new(xs, null);

    // 刻度为静态内容:启动时格式化一次,避免每帧 $"" 插值分配(同 VolumeFaderControl)。
    private static readonly double[] ScaleDbs = [0.0, -3, -12, -20, -40, -60];
    private static readonly string[] ScaleLabels = ScaleDbs.Select(db => $"{db:0}").ToArray();

    private FormattedText ScaleLabel(int i, Brush brush) => new(
        ScaleLabels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface!, 7.5, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        EnsureBrushes();

        // 布局:[ 电平条 L/R | 刻度区(刻度线 + dB 数字) ]
        const double scaleW = 20;
        double barsW = Math.Max(4, w - scaleW);
        const double gap = 2;
        double barW = (barsW - gap) / 2;
        double pad = ScaleLabel(0, _scaleText!).Height / 2;
        double yTop = pad, trackH = Math.Max(4, h - pad * 2);
        dc.PushGuidelineSet(VerticalGuidelines(0, barW, barW + gap, barsW, w));

        // 分别绘制 L / R 两个圆角槽(更具现代数字调音台 LED 柱质感)
        for (int i = 0; i < 2; i++)
        {
            var barSlot = new Rect(i * (barW + gap), yTop, barW, trackH);
            dc.DrawRoundedRectangle(_track, _trackPen, barSlot, 2, 2);
        }

        // dB 刻度:数字右对齐于刻度区;-3/-12 即黄/红分界,数字提亮呼应颜色阈值
        for (int i = 0; i < ScaleDbs.Length; i++)
        {
            double db = ScaleDbs[i];
            double y = yTop + trackH * (1 - DbToNorm(db));
            double ys = Math.Round(y) + 0.5;
            dc.DrawLine(_tickPen, new Point(barsW + 1, ys), new Point(barsW + 4, ys));
            bool hi = db is 0.0 or -3 or -12;
            var t = ScaleLabel(i, hi ? _scaleTextHi! : _scaleText!);
            dc.DrawText(t, new Point(w - t.Width, y - t.Height / 2));
        }

        for (int i = 0; i < 2; i++)
        {
            var bar = new Rect(i * (barW + gap) + 0.5, yTop, Math.Max(1, barW - 1), trackH);
            DrawLevel(dc, bar, _display[i], trackH);
            DrawPeak(dc, bar, _peak[i], trackH);
        }
        dc.Pop();

        AutomationProperties.SetName(this, "Level meter");
    }

    private void DrawLevel(DrawingContext dc, Rect bar, double level, double h)
    {
        if (level <= 0.001) return;
        double fillH = Math.Round(h * level);
        if (fillH < 1) return;

        // 用 clip 限制在填充段区域,画全高渐变矩形并叠一层分段横纹
        dc.PushClip(new RectangleGeometry(new Rect(bar.X, bar.Bottom - fillH, bar.Width, fillH), 1.5, 1.5));
        dc.DrawRectangle(_fill, null, new Rect(bar.X, bar.Y, bar.Width, h));
        dc.DrawRectangle(_segments, null, new Rect(bar.X, bar.Y, bar.Width, h));
        dc.Pop();
    }

    private void DrawPeak(DrawingContext dc, Rect bar, double peak, double h)
    {
        if (peak <= 0.001) return;
        double py = bar.Y + Math.Round(h * (1 - peak)) + 0.5;
        var pen = peak >= 1 - 3.0 / 60 ? _peakWarnPen : _peakWhitePen;
        dc.DrawLine(pen, new Point(bar.X + 0.5, py), new Point(bar.Right - 0.5, py));
    }
}

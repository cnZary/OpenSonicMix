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
/// 所有实例统一由静态 CompositionTarget.Rendering 驱动(单一订阅,窗口关闭或电平静止时解除,来信号零延迟唤醒)。
/// 最小化/不可见时暂停绘制;静态背景槽、dB 刻度文字与分段横纹几何体按尺寸预烘焙缓存。
/// 视觉:分段渐变电平条(绿→黄→红,分界处颜色连续)、右侧 dB 刻度尺(数字 + 刻度线)、双段式峰值保持线。
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    // ── 静态渲染驱动(全应用一个订阅,静音/最小化时休眠)─────────
    private static readonly List<WeakReference<MeterBar>> Active = new();
    private static volatile bool _subscribed;
    private static int _wakePending;
    private static DateTime _lastFrame = DateTime.MinValue;
    private Window? _hostWindow;
    private bool _windowMinimized;

    public MeterBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AutomationProperties.SetName(this, "Level meter");
        _display = new double[2];
        _peak = new double[2];
        _peakHoldUntil = new DateTime[2];
        IsVisibleChanged += (_, _) => UpdateSubscription();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (!ReferenceEquals(_hostWindow, win))
        {
            if (_hostWindow is not null) _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = win;
            if (_hostWindow is not null)
            {
                _hostWindow.StateChanged += OnHostWindowStateChanged;
                _windowMinimized = _hostWindow.WindowState == WindowState.Minimized;
            }
        }
        Register();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
            _hostWindow = null;
        }
        Unregister();
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        bool minimized = _hostWindow?.WindowState == WindowState.Minimized;
        if (minimized == _windowMinimized) return;
        _windowMinimized = minimized;
        UpdateSubscription();
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

    /// <summary>提交最新电平(dB)。任何线程可调用,只保留最新值;若当前渲染已休眠且有新信号则零延迟唤醒。</summary>
    public void SetLevelDb(float leftDb, float rightDb)
    {
        _targetL = leftDb;
        _targetR = rightDb;
        if (!_subscribed && !_windowMinimized)
        {
            double tl = DbToNorm(leftDb);
            double tr = DbToNorm(rightDb);
            if (Math.Abs(tl - _display[0]) > 1e-4 || Math.Abs(tr - _display[1]) > 1e-4
                || tl > _peak[0] + 1e-4 || tr > _peak[1] + 1e-4)
            {
                RequestWake();
            }
        }
    }

    private void RequestWake()
    {
        if (Interlocked.Exchange(ref _wakePending, 1) != 0) return;
        if (Dispatcher.HasShutdownStarted) { Interlocked.Exchange(ref _wakePending, 0); return; }
        if (Dispatcher.CheckAccess())
        {
            Interlocked.Exchange(ref _wakePending, 0);
            EnsureSubscribedOnUi();
        }
        else
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _wakePending, 0);
                EnsureSubscribedOnUi();
            }, DispatcherPriority.Render);
        }
    }

    private static void EnsureSubscribedOnUi()
    {
        lock (Active)
        {
            if (_subscribed || Active.Count == 0) return;
            _lastFrame = DateTime.MinValue;
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    private static double DbToNorm(double db)
    {
        // 进度 = |dB|/60 (dB≤-60→0 空,dB≥0→1 满)
        double progress = ValueMapping.MeterDbToProgress(db);
        return 1.0 - progress;
    }

    private void UpdateSubscription()
    {
        bool visible = IsVisible && !_windowMinimized && ActualHeight > 0;
        if (visible) Register(); else Unregister();
    }

    private void Register()
    {
        if (_windowMinimized) return;
        lock (Active)
        {
            bool found = false;
            foreach (var wr in Active)
                if (wr.TryGetTarget(out var m) && ReferenceEquals(m, this)) { found = true; break; }
            if (!found) Active.Add(new WeakReference<MeterBar>(this));
            if (!_subscribed && NeedsStep(DateTime.UtcNow))
            {
                _lastFrame = DateTime.MinValue;
                CompositionTarget.Rendering += OnRendering;
                _subscribed = true;
            }
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
                _lastFrame = DateTime.MinValue;
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
            bool anyActive = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (!Active[i].TryGetTarget(out var m)) { Active.RemoveAt(i); continue; }
                if (!m.IsVisible || m._windowMinimized || m.ActualHeight <= 0) continue;
                if (m.Step(now, dt)) anyActive = true;
            }
            // 所有通道均已回落静止时自动解除订阅,静音/空闲时 0% CPU/GPU 占用
            if (!anyActive && _subscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _subscribed = false;
                _lastFrame = DateTime.MinValue;
            }
        }
    }

    private bool NeedsStep(DateTime now)
    {
        double tl = DbToNorm(_targetL);
        double tr = DbToNorm(_targetR);
        return Math.Abs(tl - _display[0]) > 1e-4 || Math.Abs(tr - _display[1]) > 1e-4
            || _peak[0] > 1e-4 || _peak[1] > 1e-4
            || now <= _peakHoldUntil[0] || now <= _peakHoldUntil[1];
    }

    /// <summary>推进一步动画;返回是否仍有未收敛的动画需要下一帧继续。</summary>
    private bool Step(DateTime now, double dt)
    {
        double targetL = DbToNorm(_targetL);
        double targetR = DbToNorm(_targetR);
        bool changed = false;
        changed |= StepChannel(0, targetL, now, dt);
        changed |= StepChannel(1, targetR, now, dt);
        if (changed) InvalidateVisual();
        return changed || NeedsStep(now);
    }

    private bool StepChannel(int ch, double target, DateTime now, double dt)
    {
        bool changed = false;
        double cur = _display[ch];
        double stepUp = AttackDbPerSec * dt / 60.0;
        double stepDown = ReleaseDbPerSec * dt / 60.0;
        double next = target > cur ? Math.Min(target, cur + stepUp) : Math.Max(target, cur - stepDown);
        if (Math.Abs(next - cur) > 1e-4) { _display[ch] = next; changed = true; }
        else if (cur != target) { _display[ch] = target; if (cur > 0) changed = true; }

        // 峰值保持
        if (target >= _peak[ch] && target > 1e-4)
        {
            if (Math.Abs(target - _peak[ch]) > 1e-4) changed = true;
            _peak[ch] = target;
            _peakHoldUntil[ch] = now + PeakHold;
        }
        else if (_peak[ch] > 0 && now > _peakHoldUntil[ch])
        {
            double pkDb = -60 + _peak[ch] * 60;
            double npDb = Math.Max(-60, pkDb - PeakReleaseDbPerSec * dt);
            double np = (npDb + 60) / 60;
            if (np <= 1e-4) np = 0;
            if (Math.Abs(np - _peak[ch]) > 1e-4 || (np == 0 && _peak[ch] > 0)) { _peak[ch] = np; changed = true; }
        }
        return changed;
    }

    // ── 画刷与静态几何缓存(背景槽、刻度文字、横纹仅在尺寸/DPI 改变时构建一次)──
    private Brush? _track, _tick, _peakWhite, _peakWarn, _scaleText, _scaleTextHi;
    private Pen? _trackPen, _tickPen, _peakWhitePen, _peakWarnPen;
    private Typeface? _typeface;
    private LinearGradientBrush? _fill;      // 电平条渐变(绿→黄→红,分界连续)

    // 按控件尺寸/DPI 预烘焙的静态缓存
    private double _cachedW, _cachedH, _cachedDpi;
    private double _cachedYTop, _cachedTrackH, _cachedBarW;
    private DrawingGroup? _staticBackdrop;   // L/R 圆角槽 + 右侧刻度线 + dB 数字
    private GeometryDrawing?[] _segmentOverlays = new GeometryDrawing?[2]; // L/R 预烘焙横纹间隙

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
    }

    private static Color Scale(Color c, double f) =>
        Color.FromArgb(c.A,
            (byte)Math.Min(255, (int)(c.R * f + 0.5)),
            (byte)Math.Min(255, (int)(c.G * f + 0.5)),
            (byte)Math.Min(255, (int)(c.B * f + 0.5)));

    private static readonly double[] ScaleDbs = [0.0, -3, -12, -20, -40, -60];
    private static readonly string[] ScaleLabels = ScaleDbs.Select(db => $"{db:0}").ToArray();
    private const double Gap = 2;

    private FormattedText CreateScaleLabel(int i, Brush brush, double dpi) => new(
        ScaleLabels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface!, 7.5, brush, dpi);

    private void EnsureStaticCache(double w, double h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_staticBackdrop is not null
            && Math.Abs(w - _cachedW) < 0.05
            && Math.Abs(h - _cachedH) < 0.05
            && Math.Abs(dpi - _cachedDpi) < 0.001)
            return;

        _cachedW = w;
        _cachedH = h;
        _cachedDpi = dpi;

        const double scaleW = 20;
        double barsW = Math.Max(4, w - scaleW);
        double barW = (barsW - Gap) / 2;
        var label0 = CreateScaleLabel(0, _scaleTextHi!, dpi);
        double pad = label0.Height / 2;
        double yTop = pad, trackH = Math.Max(4, h - pad * 2);

        _cachedYTop = yTop;
        _cachedTrackH = trackH;
        _cachedBarW = barW;

        var backdrop = new DrawingGroup();
        backdrop.GuidelineSet = new GuidelineSet([0, barW, barW + Gap, barsW, w], null);
        using (var dc = backdrop.Open())
        {
            // 1. L / R 两个圆角槽
            for (int i = 0; i < 2; i++)
            {
                var barSlot = new Rect(i * (barW + Gap), yTop, barW, trackH);
                dc.DrawRoundedRectangle(_track, _trackPen, barSlot, 2, 2);
            }

            // 2. dB 刻度:数字右对齐于刻度区;-3/-12 即黄/红分界,数字提亮呼应颜色阈值
            for (int i = 0; i < ScaleDbs.Length; i++)
            {
                double db = ScaleDbs[i];
                double y = yTop + trackH * (1 - DbToNorm(db));
                double ys = Math.Round(y) + 0.5;
                dc.DrawLine(_tickPen, new Point(barsW + 1, ys), new Point(barsW + 4, ys));
                bool hi = db is 0.0 or -3 or -12;
                var t = i == 0 ? label0 : CreateScaleLabel(i, hi ? _scaleTextHi! : _scaleText!, dpi);
                dc.DrawText(t, new Point(w - t.Width, y - t.Height / 2));
            }
        }
        backdrop.Freeze();
        _staticBackdrop = backdrop;

        // 3. 预烘焙 L/R 分段横纹间隙(替代每帧 DrawingBrush TileMode.Tile 采样)
        double yBottom = yTop + trackH;
        for (int ch = 0; ch < 2; ch++)
        {
            double bx = ch * (barW + Gap) + 0.5;
            double bw = Math.Max(1, barW - 1);
            var segGeo = new StreamGeometry();
            using (var ctx = segGeo.Open())
            {
                // 与原 Viewport=(0,0,1,4.5) 瓦片中 [3..4.5] 区间完全对齐
                double firstTileY = Math.Floor(yTop / 4.5) * 4.5;
                for (double tileY = firstTileY; tileY < yBottom; tileY += 4.5)
                {
                    double sy0 = Math.Max(yTop, tileY + 3.0);
                    double sy1 = Math.Min(yBottom, tileY + 4.5);
                    if (sy1 <= sy0) continue;
                    ctx.BeginFigure(new Point(bx, sy0), true, true);
                    ctx.LineTo(new Point(bx + bw, sy0), false, false);
                    ctx.LineTo(new Point(bx + bw, sy1), false, false);
                    ctx.LineTo(new Point(bx, sy1), false, false);
                }
            }
            segGeo.Freeze();
            var drawing = new GeometryDrawing(_track, null, segGeo);
            drawing.Freeze();
            _segmentOverlays[ch] = drawing;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        EnsureBrushes();
        EnsureStaticCache(w, h);

        dc.DrawDrawing(_staticBackdrop);

        double barW = _cachedBarW, yTop = _cachedYTop, trackH = _cachedTrackH;
        for (int i = 0; i < 2; i++)
        {
            var bar = new Rect(i * (barW + Gap) + 0.5, yTop, Math.Max(1, barW - 1), trackH);
            DrawLevel(dc, i, bar, _display[i], trackH);
            DrawPeak(dc, bar, _peak[i], trackH);
        }
    }

    private void DrawLevel(DrawingContext dc, int ch, Rect bar, double level, double h)
    {
        if (level <= 0.001) return;
        double fillH = h * level;
        if (fillH < 0.5) return;

        // 轴对齐矩形 Clip 走 Direct3D 硬件 Scissor 快路径(无圆角几何抗锯齿蒙版开销),
        // 内部绘制全高冻结渐变 + 预烘焙横纹几何体
        dc.PushClip(new RectangleGeometry(new Rect(bar.X, bar.Bottom - fillH, bar.Width, fillH)));
        dc.DrawRectangle(_fill, null, bar);
        dc.DrawDrawing(_segmentOverlays[ch]);
        dc.Pop();
    }

    private void DrawPeak(DrawingContext dc, Rect bar, double peak, double h)
    {
        if (peak <= 0.001) return;
        double py = bar.Y + h * (1 - peak);
        var pen = peak >= 1 - 3.0 / 60 ? _peakWarnPen : _peakWhitePen;
        dc.DrawLine(pen, new Point(bar.X + 0.5, py), new Point(bar.Right - 0.5, py));
    }
}

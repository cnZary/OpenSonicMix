using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 双声道电平表(自绘,DrawingContext,Fluent 2 风格)。
/// 输入为协议 dB;内部以“满刻度比例 0..1”渲染(dB≤-60→0,dB≥0→1)。
/// 快攻慢放 + 峰值保持:按实际经过时间计算,不按帧数。
/// 所有实例统一由静态 CompositionTarget.Rendering 驱动(单一订阅,窗口关闭或电平静止时解除,来信号零延迟唤醒)。
/// 最小化/不可见时暂停绘制;静态背景(槽、刻度线、dB 数字)与满高渐变+横纹填充按尺寸/DPI 预烘焙为位图,
/// 条体与峰值使用保留的子 Visual,逐帧仅更新裁剪和位移,不重建整表绘制指令。
/// 视觉:分段渐变电平条(绿→黄→红,分界处颜色连续)、右侧 dB 刻度尺(数字 + 刻度线)、双段式峰值保持线。
/// </summary>
public sealed class MeterBar : FrameworkElement
{
    // ── 静态渲染驱动(全应用一个订阅,静音/最小化时休眠)─────────
    private static readonly List<WeakReference<MeterBar>> Active = new();
    private static volatile bool _subscribed;
    private static int _wakePending;
    private static int _inputVersion;
    private static TimeSpan? _lastRenderingTime;
    private static long _renderWakeTimestamp;
    private Window? _hostWindow;
    private volatile bool _registered;
    private volatile bool _windowMinimized;
    private readonly object _levelSync = new();
    private readonly DrawingVisual[] _levelVisuals = [new(), new()];
    private readonly DrawingVisual[] _peakVisuals = [new(), new()];
    private readonly RectangleGeometry[] _clipGeo = [new(), new()];
    private readonly bool[] _peakWarning = new bool[2];

    public MeterBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        AutomationProperties.SetName(this, "Level meter");
        _display = new double[2];
        _peak = new double[2];
        _peakHoldUntil = new DateTime[2];
        for (int i = 0; i < 2; i++)
        {
            _levelVisuals[i].Clip = _clipGeo[i];
            AddVisualChild(_levelVisuals[i]);
            AddVisualChild(_peakVisuals[i]);
        }
        IsVisibleChanged += (_, _) => UpdateSubscription();
        SizeChanged += (_, _) => UpdateSubscription();
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
        UpdateSubscription();
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

    /// <summary>目标电平(dB,-100=无信号)。条体使用最新值,两帧之间的最大值另存供峰值线使用。</summary>
    private float _targetL = -100f;
    private float _targetR = -100f;

    private readonly double[] _display;       // 渲染用平滑值(0..1)
    private readonly double[] _peak;          // 峰值保持(0..1)
    private readonly DateTime[] _peakHoldUntil;
    private readonly double[] _pendingPeak = new double[2];
    private readonly DateTime[] _pendingPeakHoldUntil = new DateTime[2];

    // 上升按剩余距离平滑追近,约 60ms 完成 95%;小幅变化也经过多帧,避免逐包跳变。
    private const double AttackTimeConstantSeconds = 0.020;
    private const double DisplayTolerance = 1e-4; // 0.006dB,300 DIP 条高下小于 0.03 DIP
    private const double ReleaseDbPerSec = 24;   // 条体回落,快于峰值线:停声后条先落、峰值悬空慢降
    private static readonly TimeSpan PeakHold = TimeSpan.FromMilliseconds(900);
    private const double PeakReleaseDbPerSec = 10;   // 峰值线慢速下落,始终不落于条顶之下

    /// <summary>提交最新电平(dB)。任何线程可调用;保留帧间短峰值,若渲染已休眠则立即请求唤醒。</summary>
    public void SetLevelDb(float leftDb, float rightDb) => SetLevelDb(leftDb, rightDb, DateTime.UtcNow);

    internal void SetLevelDb(float leftDb, float rightDb, DateTime now)
    {
        bool needsStep;
        lock (_levelSync)
        {
            double previousL = DbToNorm(_targetL);
            double previousR = DbToNorm(_targetR);
            double tl = DbToNorm(leftDb);
            double tr = DbToNorm(rightDb);
            // 上个目标一直保持到本次输入;从稳定峰值下降时也保留完整保持段,
            // 即使两次输入相隔很久、期间渲染已休眠。
            RetainPeak(0, Math.Max(previousL, tl), now);
            RetainPeak(1, Math.Max(previousR, tr), now);
            _targetL = leftDb;
            _targetR = rightDb;
            needsStep = NeedsStepCore(now);
        }
        Interlocked.Increment(ref _inputVersion);
        if (needsStep && !_subscribed && _registered) RequestWake();
    }

    private void RetainPeak(int ch, double peak, DateTime now)
    {
        // 隐藏/阻塞期间过期的高峰不能遮住更新的低峰,也不能在恢复绘制时重新获得保持时间。
        if (now > _pendingPeakHoldUntil[ch]) _pendingPeak[ch] = 0;
        if (peak >= _pendingPeak[ch] && peak > 1e-4)
        {
            _pendingPeak[ch] = peak;
            _pendingPeakHoldUntil[ch] = now + PeakHold;
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
            _lastRenderingTime = null;
            _renderWakeTimestamp = Stopwatch.GetTimestamp();
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
        bool visible = IsLoaded && IsVisible && !_windowMinimized && ActualWidth > 0 && ActualHeight > 0;
        if (visible) Register(); else Unregister();
    }

    private void Register()
    {
        if (_windowMinimized) return;
        lock (Active)
        {
            _registered = true;
            bool found = false;
            foreach (var wr in Active)
                if (wr.TryGetTarget(out var m) && ReferenceEquals(m, this)) { found = true; break; }
            if (!found) Active.Add(new WeakReference<MeterBar>(this));
            if (!_subscribed && NeedsStep(DateTime.UtcNow))
            {
                _lastRenderingTime = null;
                _renderWakeTimestamp = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRendering;
                _subscribed = true;
            }
        }
    }

    private void Unregister()
    {
        lock (Active)
        {
            _registered = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (!Active[i].TryGetTarget(out var m) || ReferenceEquals(m, this))
                    Active.RemoveAt(i);
            }
            if (Active.Count == 0 && _subscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _subscribed = false;
                _lastRenderingTime = null;
            }
        }
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        // 使用合成帧时间:回调可能紧挨着执行,墙钟间隔不代表实际动画帧间隔。
        var previousTime = _lastRenderingTime;
        if (previousTime.HasValue && frame.RenderingTime <= previousTime.Value) return;
        _lastRenderingTime = frame.RenderingTime;
        int inputVersion = Volatile.Read(ref _inputVersion);
        var now = DateTime.UtcNow;
        // 休眠唤醒后的第一帧仅推进实际经过的时间,不假定每块屏幕都是 60Hz。
        double dt = FrameElapsedSeconds(previousTime, frame.RenderingTime,
            previousTime.HasValue ? 0 : Stopwatch.GetElapsedTime(_renderWakeTimestamp).TotalSeconds);

        lock (Active)
        {
            bool anyActive = false;
            for (int i = Active.Count - 1; i >= 0; i--)
            {
                if (!Active[i].TryGetTarget(out var m)) { Active.RemoveAt(i); continue; }
                if (!m.IsVisible || m._windowMinimized || m.ActualHeight <= 0) continue;
                if (m.Step(now, dt)) anyActive = true;
            }
            // 稳定电平与静音均休眠;新信号直接唤醒,仍由统一渲染驱动逐帧推进。
            if (!anyActive && _subscribed)
            {
                CompositionTarget.Rendering -= OnRendering;
                _subscribed = false;
                _lastRenderingTime = null;
                // 提交电平与解除订阅交错时也不能丢掉最后一次唤醒。
                if (inputVersion != Volatile.Read(ref _inputVersion)) EnsureSubscribedOnUi();
            }
        }
    }

    internal static double FrameElapsedSeconds(TimeSpan? previous, TimeSpan current, double wakeElapsedSeconds) =>
        Math.Clamp(previous.HasValue ? (current - previous.Value).TotalSeconds : wakeElapsedSeconds, 0, 0.1);

    private bool NeedsStep(DateTime now)
    {
        lock (_levelSync) return NeedsStepCore(now);
    }

    private bool NeedsStepCore(DateTime now)
    {
        double tl = DbToNorm(_targetL);
        double tr = DbToNorm(_targetR);
        return Math.Abs(tl - _display[0]) > 1e-4 || Math.Abs(tr - _display[1]) > 1e-4
            || Math.Abs(_peak[0] - tl) > 1e-4 || Math.Abs(_peak[1] - tr) > 1e-4
            || (_pendingPeakHoldUntil[0] >= now && _pendingPeak[0] > _peak[0] + 1e-4)
            || (_pendingPeakHoldUntil[1] >= now && _pendingPeak[1] > _peak[1] + 1e-4);
    }

    /// <summary>推进一步动画;返回是否仍有未收敛的动画需要下一帧继续。</summary>
    private bool Step(DateTime now, double dt)
    {
        lock (_levelSync)
        {
            double attackBlend = 1 - Math.Exp(-dt / AttackTimeConstantSeconds);
            bool changed = StepChannel(0, DbToNorm(_targetL), now, dt, attackBlend);
            changed |= StepChannel(1, DbToNorm(_targetR), now, dt, attackBlend);
            if (changed) UpdateDynamicVisuals();
            return changed || NeedsStepCore(now);
        }
    }

    private bool StepChannel(int ch, double target, DateTime now, double dt, double attackBlend)
    {
        bool changed = false;
        // 接收线程仅记录数据;峰值 Visual 的更新仍在 UI 帧上执行。
        if (_pendingPeakHoldUntil[ch] >= now && _pendingPeak[ch] >= _peak[ch] && _pendingPeak[ch] > 1e-4)
        {
            changed = _pendingPeak[ch] != _peak[ch];
            _peak[ch] = _pendingPeak[ch];
            if (_pendingPeakHoldUntil[ch] > _peakHoldUntil[ch])
                _peakHoldUntil[ch] = _pendingPeakHoldUntil[ch];
        }
        _pendingPeak[ch] = 0;
        double cur = _display[ch];
        double stepDown = ReleaseDbPerSec * dt / 60.0;
        double next = target > cur ? cur + (target - cur) * attackBlend : Math.Max(target, cur - stepDown);
        // 只在剩余距离小于视觉精度时收敛,避免指数尾部持续请求帧。
        // 不能根据本帧移动量判定收敛,极小 dt 的正常运动也会很小;dt=0 不吸附。
        if (dt > 0 && target > cur && target - next <= DisplayTolerance) next = target;
        if (next != cur) { _display[ch] = next; changed = true; }

        // 峰值保持
        if (target >= _peak[ch] && target > 1e-4)
        {
            if (target != _peak[ch]) changed = true;
            _peak[ch] = target;
            _peakHoldUntil[ch] = now + PeakHold;
        }
        else if (_peak[ch] > 0 && now > _peakHoldUntil[ch])
        {
            double pkDb = -60 + _peak[ch] * 60;
            double npDb = Math.Max(-60, pkDb - PeakReleaseDbPerSec * dt);
            double np = (npDb + 60) / 60;
            if (np <= 1e-4) np = 0;
            if (np != _peak[ch]) { _peak[ch] = np; changed = true; }
        }
        return changed;
    }

    // ── 画刷与静态几何缓存(背景槽、刻度文字、横纹仅在尺寸/DPI 改变时构建一次)──
    private Brush? _track, _tick, _peakWhite, _peakWarn, _scaleText, _scaleTextHi;
    private Pen? _trackPen, _tickPen, _peakWhitePen, _peakWarnPen;
    private Typeface? _typeface;
    private LinearGradientBrush? _fill;      // 电平条渐变(绿→黄→红,分界连续)

    // 按控件尺寸/DPI 预烘焙的位图缓存
    private double _cachedW, _cachedH, _cachedDpi;
    private double _cachedYTop, _cachedTrackH, _cachedBarW;
    private RenderTargetBitmap? _backdropBmp;  // 整表背景:L/R 圆角槽 + 右侧刻度线 + dB 数字
    private RenderTargetBitmap? _fillBmp;      // 单声道满高填充:渐变 + 横纹间隙(两声道内容一致,共用一张,bar 局部坐标)
    private double _backdropDestW, _backdropDestH; // 位图目标绘制尺寸(= 像素/dpi,保证 1:1 不缩放模糊)
    private double _fillDestW, _fillDestH;
    private bool _dynamicReady;

    private void EnsureBrushes()
    {
        if (_fill is not null) return;
        _track = this.GetDrawingBrush("BrushMeterTrack");
        _tick = this.GetDrawingBrush("BrushTickMajor");
        _peakWhite = this.GetDrawingBrush("BrushThumbLine");
        _peakWarn = this.GetDrawingBrush("BrushWarn");
        _scaleText = this.GetDrawingBrush("BrushTextTertiary");
        _scaleTextHi = this.GetDrawingBrush("BrushTextSecondary");

        var trackBorder = this.GetDrawingBrush("BrushBorderLight");
        _trackPen = new Pen(trackBorder, 1);
        _tickPen = new Pen(_tick, 1);
        _peakWhitePen = new Pen(_peakWhite, 1.2);
        _peakWarnPen = new Pen(_peakWarn, 1.4);
        _trackPen.Freeze(); _tickPen.Freeze(); _peakWhitePen.Freeze(); _peakWarnPen.Freeze();
        _typeface = new Typeface((FontFamily)FindResource("FontMono"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

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

    private static readonly double[] ScaleDbs = [0.0, -3, -6, -12, -18, -24, -36, -48, -60];
    private static readonly string[] ScaleLabels = ScaleDbs.Select(db => $"{db:0}").ToArray();
    private const double Gap = 2;

    private FormattedText CreateScaleLabel(int i, Brush brush, double dpi) => new(
        ScaleLabels[i], System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        _typeface!, 8, brush, dpi);

    private void EnsureStaticCache(double w, double h)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_backdropBmp is not null
            && Math.Abs(w - _cachedW) < 0.05
            && Math.Abs(h - _cachedH) < 0.05
            && Math.Abs(dpi - _cachedDpi) < 0.001 && _dynamicReady)
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

        // 背景一次性烘焙为位图(设备像素,向上取整保证内容不被裁;绘制时按 像素/dpi 目标尺寸 1:1 blit)
        int pixW = Math.Max(1, (int)Math.Ceiling(w * dpi));
        int pixH = Math.Max(1, (int)Math.Ceiling(h * dpi));
        var backVis = new DrawingVisual();
        using (var bdc = backVis.RenderOpen())
            bdc.DrawDrawing(backdrop);
        var backBmp = new RenderTargetBitmap(pixW, pixH, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        backBmp.Render(backVis);
        backBmp.Freeze();
        _backdropBmp = backBmp;
        _backdropDestW = pixW / dpi;
        _backdropDestH = pixH / dpi;

        // 满高填充(渐变 + 横纹间隙)烘焙为单张位图,两声道内容一致共用;bar 局部坐标 [0,bw]×[0,trackH]
        double yBottom = yTop + trackH;
        double bw = Math.Max(1, barW - 1);
        var segGeo = new StreamGeometry();
        using (var ctx = segGeo.Open())
        {
            // 与原 Viewport=(0,0,1,4.5) 瓦片中 [3..4.5] 区间完全对齐
            double firstTileY = Math.Floor(yTop / 4.5) * 4.5;
            for (double tileY = firstTileY; tileY < yBottom; tileY += 4.5)
            {
                double sy0 = Math.Max(0, tileY + 3.0 - yTop);
                double sy1 = Math.Min(trackH, tileY + 4.5 - yTop);
                if (sy1 <= sy0) continue;
                ctx.BeginFigure(new Point(0, sy0), true, true);
                ctx.LineTo(new Point(bw, sy0), false, false);
                ctx.LineTo(new Point(bw, sy1), false, false);
                ctx.LineTo(new Point(0, sy1), false, false);
            }
        }
        segGeo.Freeze();
        var segDrawing = new GeometryDrawing(_track, null, segGeo);
        segDrawing.Freeze();

        int fpixW = Math.Max(1, (int)Math.Ceiling(bw * dpi));
        int fpixH = Math.Max(1, (int)Math.Ceiling(trackH * dpi));
        var fillVis = new DrawingVisual();
        using (var fdc = fillVis.RenderOpen())
        {
            fdc.DrawRectangle(_fill, null, new Rect(0, 0, bw, trackH));
            fdc.DrawDrawing(segDrawing);
        }
        var fillBmp = new RenderTargetBitmap(fpixW, fpixH, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        fillBmp.Render(fillVis);
        fillBmp.Freeze();
        _fillBmp = fillBmp;
        _fillDestW = fpixW / dpi;
        _fillDestH = fpixH / dpi;

        for (int i = 0; i < 2; i++)
        {
            using (var dc = _levelVisuals[i].RenderOpen())
                dc.DrawImage(_fillBmp, new Rect(0, 0, _fillDestW, _fillDestH));
            _levelVisuals[i].Offset = new Vector(i * (barW + Gap) + 0.5, yTop);
            DrawPeakVisual(i, _peak[i] >= 1 - 3.0 / 60);
        }
        _dynamicReady = true;
        UpdateDynamicVisuals();
    }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _levelVisuals[0],
        1 => _peakVisuals[0],
        2 => _levelVisuals[1],
        3 => _peakVisuals[1],
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10)
        {
            for (int i = 0; i < 2; i++)
            {
                _levelVisuals[i].Opacity = 0;
                _peakVisuals[i].Opacity = 0;
            }
            _dynamicReady = false;
            return;
        }
        EnsureBrushes();
        EnsureStaticCache(w, h);

        dc.DrawImage(_backdropBmp, new Rect(0, 0, _backdropDestW, _backdropDestH));

    }

    private void UpdateDynamicVisuals()
    {
        if (!_dynamicReady) return;
        double barW = _cachedBarW, trackH = _cachedTrackH;
        for (int i = 0; i < 2; i++)
        {
            double fillH = trackH * _display[i];
            bool showLevel = _display[i] > 0.001 && fillH >= 0.5;
            _levelVisuals[i].Opacity = showLevel ? 1 : 0;
            if (showLevel)
            {
                var clip = new Rect(0, trackH - fillH, Math.Max(1, barW - 1), fillH);
                if (_clipGeo[i].Rect != clip) _clipGeo[i].Rect = clip;
            }

            bool showPeak = _peak[i] > 0.001;
            _peakVisuals[i].Opacity = showPeak ? 1 : 0;
            if (showPeak)
            {
                bool warning = _peak[i] >= 1 - 3.0 / 60;
                if (_peakWarning[i] != warning) DrawPeakVisual(i, warning);
                _peakVisuals[i].Offset = new Vector(i * (barW + Gap) + 0.5,
                    _cachedYTop + trackH * (1 - _peak[i]));
            }
        }
    }

    private void DrawPeakVisual(int channel, bool warning)
    {
        _peakWarning[channel] = warning;
        using var dc = _peakVisuals[channel].RenderOpen();
        dc.DrawLine(warning ? _peakWarnPen : _peakWhitePen,
            new Point(0.5, 0), new Point(Math.Max(1, _cachedBarW - 1) - 0.5, 0));
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Threading;
using SonicCube.Protocol.Models;

namespace SonicCube.Panel.Controls;

/// <summary>
/// 双声道电平表(自绘,DrawingContext)。
/// 输入为协议 dB;内部以“满刻度比例 0..1”渲染(dB≤-60→0,dB≥0→1)。
/// 快攻慢放 + 峰值保持:按实际经过时间计算,不按帧数。
/// 所有实例统一由静态 CompositionTarget.Rendering 驱动(单一订阅,窗口关闭时解除)。
/// 最小化/不可见时暂停绘制。
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
        double curDb = -60 + cur * 60;         // 0..1 → -60..0 dB
        double targetDb = -60 + target * 60;
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

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;
        int bars = 2;
        double gap = 2;
        double barW = (w - gap * (bars - 1)) / bars;
        for (int i = 0; i < bars; i++)
            DrawBar(dc, new Rect(i * (barW + gap), 0, barW, h), _display[i], _peak[i]);
        AutomationProperties.SetName(this, "Level meter");
    }

    private void DrawBar(DrawingContext dc, Rect r, double level, double peak)
    {
        // 背景
        dc.DrawRectangle((Brush)FindResource("BrushPanelDeep"), null, r);
        var redBrush = new SolidColorBrush((Color)FindResource("ColorMeterRed"));
        var yellowBrush = new SolidColorBrush((Color)FindResource("ColorMeterYellow"));
        var green = (Brush)FindResource("BrushOn");
        if (level > 0.001)
        {
            // 分段颜色:≤-12dB 绿,-12~-3 黄,>-3 红(分界相对满刻度)
            double yTop = 1 - 3.0 / 60;      // -3dB 归一
            double yMid = 1 - 12.0 / 60;     // -12dB 归一
            double l = level;
            dc.DrawRectangle(green, null, new Rect(r.X, r.Bottom - r.Height * Math.Min(l, yMid), r.Width, r.Height * Math.Min(l, yMid)));
            if (l > yMid)
                dc.DrawRectangle(yellowBrush, null, new Rect(r.X, r.Bottom - r.Height * Math.Min(l, yTop), r.Width, r.Height * (Math.Min(l, yTop) - yMid)));
            if (l > yTop)
                dc.DrawRectangle(redBrush, null, new Rect(r.X, r.Bottom - r.Height * l, r.Width, r.Height * (l - yTop)));
        }
        // 峰值线
        if (peak > 0.001)
        {
            double py = r.Bottom - r.Height * peak;
            dc.DrawLine(new Pen((Brush)FindResource("BrushWarn"), 1.5), new Point(r.X, py), new Point(r.Right, py));
        }
    }
}

namespace SonicCube.Protocol.Models;

/// <summary>
/// UI ↔ 协议数值换算(见 re/ui_controls_report.md 第 1.1、4.3 节与附录)。
/// </summary>
public static class ValueMapping
{
    // ── 音量推子 dB 分段线性映射 ─────────────────────────────
    // xData = 协议 dB 值,yData = UI 等距位置。
    private static readonly double[] FaderX = { -51, -30, -20, -10, -5, 0, 5, 10 };
    private static readonly double[] FaderY =
        { -51, -42.2857143, -33.5714286, -24.8571429, -16.1428572, -7.42857145, 1.28571426, 10 };

    /// <summary>协议 dB → UI 等距位置(分段线性插值)。</summary>
    public static double FaderDbToPosition(double db) => MapPiecewise(FaderX, FaderY, db);

    /// <summary>UI 等距位置 → 协议 dB(分段线性插值)。</summary>
    public static double FaderPositionToDb(double position) => MapPiecewise(FaderY, FaderX, position);

    /// <summary>推子显示文本:小于 -50.18 dB 显示 "-∞",否则四舍五入到整数 dB(避免 "-0")。</summary>
    public static string FaderText(double db)
    {
        if (db < -50.18) return "-∞";
        int v = (int)Math.Round(db);
        return v == 0 ? "0" : v.ToString("0");
    }

    // ── SEND 发送量 ──────────────────────────────────────────
    /// <summary>SEND 协议值 = UI 值 / 100(UI 0~100 → 协议 0.0~1.0)。</summary>
    public static float SendToProtocol(double uiValue) => (float)(uiValue / 100.0);
    public static double SendToUi(float protocolValue) => protocolValue * 100.0;

    // ── EQ 拖拽点对数频率轴 ──────────────────────────────────
    // freq = 10^(dx/(W·0.92)·3 + log10(2) + 1),clamp 20~20000 Hz。
    // gain = 20 − dy/(H·0.71)·40,clamp ±20 dB。
    private const double EqFreqK = 3.0;                 // 指数跨度
    private const double EqFreqBase = 1.0 + 0.3010299956639812; // log10(2)+1

    /// <summary>像素横坐标 dx / 绘制宽 W → 频率 Hz(20~20000)。</summary>
    public static double EqPixelToFreq(double dx, double width)
    {
        double f = Math.Pow(10.0, dx / (width * 0.92) * EqFreqK + EqFreqBase);
        return Math.Clamp(f, 20.0, 20000.0);
    }

    /// <summary>频率 Hz → 归一化横坐标(0~1,用于回显)。</summary>
    public static double EqFreqToNorm(double freq)
    {
        freq = Math.Clamp(freq, 20.0, 20000.0);
        double norm = (Math.Log10(freq) - EqFreqBase) / EqFreqK * 0.92;
        return Math.Clamp(norm, 0.0, 1.0);
    }

    /// <summary>像素纵坐标 dy / 绘制高 H → 增益 dB(-20~+20)。</summary>
    public static double EqPixelToGain(double dy, double height)
    {
        double g = 20.0 - dy / (height * 0.71) * 40.0;
        return Math.Clamp(g, -20.0, 20.0);
    }

    /// <summary>增益 dB → 归一化纵坐标(0~1,用于回显)。</summary>
    public static double EqGainToNorm(double gain)
    {
        gain = Math.Clamp(gain, -20.0, 20.0);
        double norm = (20.0 - gain) / 40.0 * 0.71;
        return Math.Clamp(norm, 0.0, 1.0);
    }

    /// <summary>Global EQ Volume:协议 = UI + 50(UI -50~+50 ↔ 协议 0~100)。</summary>
    public static float GlobalEqVolToProtocol(double uiValue) => (float)(uiValue + 50.0);
    public static double GlobalEqVolToUi(float protocolValue) => protocolValue - 50.0;

    // ── 电平表 ──────────────────────────────────────────────
    /// <summary>电平 dB → 进度(dB≤-60 → 1 空;dB≥0 → 0 满;否则 |dB|/60)。</summary>
    public static double MeterDbToProgress(double db)
    {
        if (db <= -60) return 1.0;
        if (db >= 0) return 0.0;
        return Math.Abs(db) / 60.0;
    }

    private static double MapPiecewise(double[] xs, double[] ys, double x)
    {
        if (x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];
        for (int i = 0; i < xs.Length - 1; i++)
        {
            if (x <= xs[i + 1])
            {
                double t = (x - xs[i]) / (xs[i + 1] - xs[i]);
                return ys[i] + t * (ys[i + 1] - ys[i]);
            }
        }
        return ys[^1];
    }
}

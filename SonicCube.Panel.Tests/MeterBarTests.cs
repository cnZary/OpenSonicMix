using System.Reflection;
using System.Windows;
using System.Windows.Media;
using SonicCube.Panel.Controls;

[assembly: DoNotParallelize]

namespace SonicCube.Panel.Tests;

[STATestClass]
public sealed class MeterBarTests
{
    private const double Tolerance = 1e-12;
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.000001)]
    [DataRow(0.00001)]
    [DataRow(0.0001)]
    [DataRow(0.0002)]
    [DataRow(1.0 / 60)]
    [DataRow(1.0 / 144)]
    [DataRow(1.0 / 240)]
    public void Attack_AdvancesOnlyByElapsedTime(double dt)
    {
        var meter = CreateMeter(-12, -18, 0.1);
        Step(meter, Now, dt);
        Assert.AreEqual(0.1 + 4 * dt, Display(meter)[0], Tolerance,
            $"Attack must remain at 240 dB/s for dt={dt:R}.");
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.000001)]
    [DataRow(0.00001)]
    [DataRow(0.0001)]
    [DataRow(0.0002)]
    [DataRow(1.0 / 60)]
    [DataRow(1.0 / 144)]
    [DataRow(1.0 / 240)]
    public void Release_AdvancesOnlyByElapsedTime(double dt)
    {
        var meter = CreateMeter(-36, -42, 0.8);
        Step(meter, Now, dt);
        Assert.AreEqual(0.8 - 0.4 * dt, Display(meter)[0], Tolerance,
            $"Release must remain at 24 dB/s for dt={dt:R}.");
    }

    [TestMethod]
    [DataRow(0.39995, -36.0)]
    [DataRow(0.40005, -36.0)]
    public void ZeroElapsedTime_DoesNotSnapEvenWithinConvergenceTolerance(double initial, double targetDb)
    {
        var meter = CreateMeter((float)targetDb, (float)targetDb, initial);
        Step(meter, Now, 0);
        Assert.AreEqual(initial, Display(meter)[0], Tolerance);
    }

    [TestMethod]
    public void TinyAttack_ReportsChangeWhenOnlyDisplayMoves()
    {
        var meter = CreateMeter(-12, -18, 0);
        // Keep peak stationary so it cannot mask a missing display-change notification.
        Peak(meter)[0] = 0.8;
        Peak(meter)[1] = 0.7;
        bool changed = (bool)Method("StepChannel").Invoke(meter, [0, 0.8, Now, 0.000001])!;
        Assert.IsTrue(changed, "Any nonzero display movement must update the retained visuals.");
        Assert.AreEqual(0.000004, Display(meter)[0], Tolerance);
    }

    [TestMethod]
    public void TinyAttack_EventuallyMakesRetainedFillVisible()
    {
        var meter = CreateMeter(-12, -18, 0);
        Peak(meter)[0] = 0.8;
        Peak(meter)[1] = 0.7;
        InitializeVisuals(meter);
        var fill = (DrawingVisual)VisualTreeHelper.GetChild(meter, 0);
        Assert.AreEqual(0.0, fill.Opacity);

        for (int i = 0; i < 1_000; i++) Step(meter, Now.AddTicks(i * 10), 0.000001);

        Assert.AreEqual(0.004, Display(meter)[0], Tolerance);
        Assert.AreEqual(1.0, fill.Opacity, "A visible display value must wake the retained fill.");
        Assert.IsGreaterThanOrEqualTo(0.5, ((RectangleGeometry)fill.Clip).Rect.Height);
    }

    [TestMethod]
    public void TinyRelease_TowardSilenceKeepsRetainedFillVisible()
    {
        var meter = CreateMeter(-100, -100, 0.8);
        Peak(meter)[0] = Peak(meter)[1] = 0.8;
        InitializeVisuals(meter);
        var fill = (DrawingVisual)VisualTreeHelper.GetChild(meter, 0);
        Assert.AreEqual(1.0, fill.Opacity);

        Step(meter, Now, 0.0001);

        Assert.AreEqual(0.79996, Display(meter)[0], Tolerance);
        Assert.AreEqual(1.0, fill.Opacity, "A submillisecond silent packet must not erase the bar.");
    }

    [TestMethod]
    [DataRow(0.0)]
    [DataRow(0.000001)]
    [DataRow(0.00001)]
    [DataRow(0.0001)]
    public void PeakRelease_AccumulatesTinyElapsedTimeAfterHold(double dt)
    {
        var meter = CreateMeter(-100, -100, 0);
        Peak(meter)[0] = 0.8;
        Hold(meter)[0] = Now.AddSeconds(-1);

        Step(meter, Now, dt);

        Assert.AreEqual(0.8 - 10 * dt / 60, Peak(meter)[0], Tolerance,
            "Peak release must retain submillisecond movement after its hold.");
    }

    [TestMethod]
    [DataRow(60)]
    [DataRow(144)]
    [DataRow(240)]
    public void NormalFrameRates_ConvergeAndPreservePeakHold(int frameRate)
    {
        var meter = CreateMeter(-12, -18, 0);
        double dt = 1.0 / frameRate;
        for (int i = 0; i < frameRate; i++) Step(meter, Now.AddSeconds(i * dt), dt);
        Assert.AreEqual(0.8, Display(meter)[0], Tolerance);
        Assert.AreEqual(0.7, Display(meter)[1], Tolerance);

        var drop = Now.AddSeconds(1);
        meter.SetLevelDb(-100, -100);
        Hold(meter)[0] = Hold(meter)[1] = drop.AddMilliseconds(900);
        for (int i = 0; i < frameRate / 2; i++) Step(meter, drop.AddSeconds(i * dt), dt);
        Assert.AreEqual(0.8, Peak(meter)[0], Tolerance, "Peak must not release during its hold.");

        bool active = true;
        for (int i = 0; i < frameRate * 10; i++) active = Step(meter, drop.AddSeconds(0.5 + i * dt), dt);
        CollectionAssert.AreEqual(new double[] { 0, 0 }, Display(meter));
        CollectionAssert.AreEqual(new double[] { 0, 0 }, Peak(meter));
        Assert.IsFalse(active, "Settled silence must stop requesting animation frames.");
    }

    private static MeterBar CreateMeter(float leftDb, float rightDb, double initial)
    {
        var meter = new MeterBar();
        meter.SetLevelDb(leftDb, rightDb);
        Display(meter)[0] = Display(meter)[1] = initial;
        Hold(meter)[0] = Hold(meter)[1] = Now.AddSeconds(1);
        return meter;
    }

    private static bool Step(MeterBar meter, DateTime now, double dt) =>
        (bool)Method("Step").Invoke(meter, [now, dt])!;

    private static MethodInfo Method(string name) => typeof(MeterBar).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Field<T>(MeterBar meter, string name) => (T)typeof(MeterBar).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(meter)!;
    private static double[] Display(MeterBar meter) => Field<double[]>(meter, "_display");
    private static double[] Peak(MeterBar meter) => Field<double[]>(meter, "_peak");
    private static DateTime[] Hold(MeterBar meter) => Field<DateTime[]>(meter, "_peakHoldUntil");

    private static void InitializeVisuals(MeterBar meter)
    {
        meter.Resources["FontMono"] = new FontFamily("Consolas");
        foreach (string key in new[] { "BrushMeterTrack", "BrushTickMajor", "BrushThumbLine", "BrushWarn", "BrushTextTertiary", "BrushTextSecondary", "BrushBorderLight" })
            meter.Resources[key] = Brushes.White;
        meter.Resources["ColorMeterGreen"] = Colors.Green;
        meter.Resources["ColorMeterYellow"] = Colors.Yellow;
        meter.Resources["ColorMeterRed"] = Colors.Red;
        meter.Measure(new Size(38, 300));
        meter.Arrange(new Rect(0, 0, 38, 300));
        using var context = new DrawingVisual().RenderOpen();
        Method("OnRender").Invoke(meter, [context]);
    }
}

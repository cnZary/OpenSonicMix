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
    public void Attack_MovesMonotonicallyWithoutJumpingToTarget(double dt)
    {
        var meter = CreateMeter(-12, -18, 0.1);
        Step(meter, Now, dt);
        if (dt == 0)
            Assert.AreEqual(0.1, Display(meter)[0], Tolerance);
        else
            Assert.IsGreaterThan(0.1, Display(meter)[0]);
        Assert.IsLessThan(0.8, Display(meter)[0],
            $"An upward change must retain interpolation between frames for dt={dt:R}.");
    }

    [TestMethod]
    [DataRow(60)]
    [DataRow(144)]
    [DataRow(240)]
    public void Attack_ElapsedTimeIsIndependentOfFramePartition(int frameRate)
    {
        var partitioned = CreateMeter(-12, -18, 0.1);
        var singleStep = CreateMeter(-12, -18, 0.1);
        double dt = 1.0 / frameRate;
        // Keep the target far from the subpixel settling threshold in both paths.
        for (int frame = 1; frame <= 3; frame++)
            Step(partitioned, Now.AddSeconds(frame * dt), dt);
        Step(singleStep, Now.AddSeconds(3 * dt), 3 * dt);
        Assert.AreEqual(Display(singleStep)[0], Display(partitioned)[0], Tolerance);
        Assert.AreEqual(Display(singleStep)[1], Display(partitioned)[1], Tolerance);
    }

    [TestMethod]
    [DataRow(60)]
    [DataRow(144)]
    [DataRow(240)]
    public void SmallRise_UsesSeveralFramesAtNormalRefreshRates(int frameRate)
    {
        var meter = CreateMeter(-18, -18, 0.6); // -24 dB -> -18 dB
        double dt = 1.0 / frameRate;
        double previous = Display(meter)[0];
        for (int frame = 1; frame <= 3; frame++)
        {
            Step(meter, Now.AddSeconds(frame * dt), dt);
            Assert.IsGreaterThan(previous, Display(meter)[0], "Every attack frame must visibly progress.");
            Assert.IsLessThan(0.7, Display(meter)[0], "A small rise must not snap to its target in one frame.");
            previous = Display(meter)[0];
        }
    }

    [TestMethod]
    [DataRow(144)]
    [DataRow(240)]
    public void FirstFrameAfterWake_UsesActualElapsedTimeAtHighRefreshRates(int frameRate)
    {
        double expectedElapsed = 1.0 / frameRate;
        double dt = MeterBar.FrameElapsedSeconds(null, TimeSpan.FromSeconds(10), expectedElapsed);
        Assert.AreEqual(expectedElapsed, dt, Tolerance,
            "Waking a meter must not assume a 16 ms frame on a high refresh display.");
        var meter = CreateMeter(0, 0, 0);
        Step(meter, Now.AddSeconds(dt), dt);
        Assert.IsGreaterThan(0.0, Display(meter)[0]);
        Assert.IsLessThan(0.30, Display(meter)[0],
            "The first high refresh frame must not jump through several frames of attack.");
    }

    [TestMethod]
    public void SubsequentFrames_UseCompositionElapsedTimeInsteadOfWakeClock()
    {
        double dt = MeterBar.FrameElapsedSeconds(
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(107), 5);
        Assert.AreEqual(0.007, dt, Tolerance);
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
        bool changed = (bool)Method("StepChannel").Invoke(meter, [0, 0.8, Now, 0.000001, 1 - Math.Exp(-0.000001 / 0.020)])!;
        Assert.IsTrue(changed, "Any nonzero display movement must update the retained visuals.");
        Assert.IsGreaterThan(0.0, Display(meter)[0]);
        Assert.IsLessThan(0.0001, Display(meter)[0], "A microsecond must not produce a visible jump.");
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

        Assert.IsGreaterThan(0.03, Display(meter)[0]);
        Assert.IsLessThan(0.05, Display(meter)[0]);
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
        meter.SetLevelDb(-100, -100, drop);
        Hold(meter)[0] = Hold(meter)[1] = drop.AddMilliseconds(900);
        for (int i = 0; i < frameRate / 2; i++) Step(meter, drop.AddSeconds(i * dt), dt);
        Assert.AreEqual(0.8, Peak(meter)[0], Tolerance, "Peak must not release during its hold.");

        bool active = true;
        for (int i = 0; i < frameRate * 10; i++) active = Step(meter, drop.AddSeconds(0.5 + i * dt), dt);
        CollectionAssert.AreEqual(new double[] { 0, 0 }, Display(meter));
        CollectionAssert.AreEqual(new double[] { 0, 0 }, Peak(meter));
        Assert.IsFalse(active, "Settled silence must stop requesting animation frames.");
    }

    [TestMethod]
    [DataRow(60, -12.0)]
    [DataRow(144, -12.0)]
    [DataRow(240, -12.0)]
    [DataRow(60, 0.0)]
    [DataRow(144, 0.0)]
    [DataRow(240, 0.0)]
    public void Attack_Reaches95PercentWithin60MillisecondsPlusOneFrame(int frameRate, double targetDb)
    {
        var meter = CreateMeter((float)targetDb, -100, 0);
        double dt = 1.0 / frameRate;
        double target = (targetDb + 60) / 60;
        int frames = 0;
        while (Display(meter)[0] < target * 0.95 && frames <= frameRate)
        {
            frames++;
            Step(meter, Now.AddSeconds(frames * dt), dt);
        }
        Assert.IsGreaterThanOrEqualTo(target * 0.95, Display(meter)[0]);
        Assert.IsLessThanOrEqualTo(target, Display(meter)[0]);
        Assert.IsLessThanOrEqualTo(0.060 + dt + Tolerance, frames * dt,
            $"At {frameRate} Hz the attack took {frames * dt * 1000:F3} ms.");
    }

    [TestMethod]
    [DataRow(60)]
    [DataRow(144)]
    [DataRow(240)]
    public void SteadyLevel_SettlesExactlyAndStopsRequestingFrames(int frameRate)
    {
        var meter = CreateMeter(-12, -18, 0);
        double dt = 1.0 / frameRate;
        bool active = true;
        for (int frame = 1; frame <= frameRate; frame++)
            active = Step(meter, Now.AddSeconds(frame * dt), dt);

        Assert.AreEqual(0.8, Display(meter)[0], Tolerance);
        Assert.AreEqual(0.7, Display(meter)[1], Tolerance);
        Assert.IsFalse(active, "The exponential tail must not keep steady meters rendering indefinitely.");
        Assert.IsFalse(NeedsStep(meter, Now.AddSeconds(1)));
    }

    [TestMethod]
    public void LowerTargetDuringAttack_ImmediatelyReleasesWithoutFollowingOldTarget()
    {
        var meter = CreateMeter(0, 0, 0.3);
        Step(meter, Now.AddMilliseconds(10), 0.010);
        double beforeDrop = Display(meter)[0];
        Assert.IsGreaterThan(0.4, beforeDrop);

        meter.SetLevelDb(-36, -36, Now.AddMilliseconds(11));
        Step(meter, Now.AddMilliseconds(21), 0.010);

        Assert.AreEqual(beforeDrop - 24 * 0.010 / 60, Display(meter)[0], Tolerance,
            "The latest lower target must switch directly to the unchanged release rate.");
        for (int frame = 1; frame <= 100; frame++)
        {
            double previous = Display(meter)[0];
            Step(meter, Now.AddMilliseconds(21 + 10 * frame), 0.010);
            Assert.IsLessThanOrEqualTo(previous, Display(meter)[0]);
            Assert.IsGreaterThanOrEqualTo(0.4, Display(meter)[0]);
        }
        Assert.AreEqual(0.4, Display(meter)[0], Tolerance);
    }

    [TestMethod]
    public void ShortPeakBeforeFrame_UsesLatestBarTargetAndRetainsEachChannelMaximum()
    {
        var meter = new MeterBar();
        meter.SetLevelDb(-6, -24, Now);
        meter.SetLevelDb(-18, -3, Now.AddMilliseconds(1));
        meter.SetLevelDb(-36, -42, Now.AddMilliseconds(2));

        Step(meter, Now.AddMilliseconds(5), 0.016);

        var latestOnly = CreateMeter(-36, -42, 0);
        Step(latestOnly, Now.AddMilliseconds(5), 0.016);
        Assert.AreEqual(Display(latestOnly)[0], Display(meter)[0], Tolerance);
        Assert.AreEqual(Display(latestOnly)[1], Display(meter)[1], Tolerance);
        Assert.AreEqual(0.9, Peak(meter)[0], Tolerance);
        Assert.AreEqual(0.95, Peak(meter)[1], Tolerance);
        Assert.AreEqual(Now.AddMilliseconds(901), Hold(meter)[0]);
        Assert.AreEqual(Now.AddMilliseconds(902), Hold(meter)[1]);
    }

    [TestMethod]
    public void PeakOnlyInput_WakesIdleMeterAndUpdatesRetainedVisualAtZeroDt()
    {
        var meter = new MeterBar();
        InitializeVisuals(meter);
        var peak = (DrawingVisual)VisualTreeHelper.GetChild(meter, 1);
        Assert.AreEqual(0.0, peak.Opacity);
        Assert.IsFalse(NeedsStep(meter, Now));
        meter.SetLevelDb(-6, -100, Now);
        meter.SetLevelDb(-100, -100, Now.AddMilliseconds(1));

        Assert.IsTrue(NeedsStep(meter, Now.AddMilliseconds(2)), "A peak-only change must request a frame.");
        Assert.IsTrue(Step(meter, Now.AddMilliseconds(2), 0));

        CollectionAssert.AreEqual(new double[] { 0, 0 }, Display(meter));
        Assert.AreEqual(0.9, Peak(meter)[0], Tolerance);
        Assert.AreEqual(1.0, peak.Opacity, "The retained peak visual must become visible on the UI step.");
    }

    [TestMethod]
    public void ShortPeak_HoldUsesReceiptTimeAndPreservesReleaseSpeed()
    {
        var meter = new MeterBar();
        meter.SetLevelDb(-12, -100, Now);
        meter.SetLevelDb(-100, -100, Now.AddMilliseconds(1));
        Step(meter, Now.AddMilliseconds(100), 0.016);

        Assert.AreEqual(Now.AddMilliseconds(901), Hold(meter)[0], "First drawing must not restart the hold.");
        Step(meter, Now.AddMilliseconds(901), 0.001);
        Assert.AreEqual(0.8, Peak(meter)[0], Tolerance);
        Step(meter, Now.AddMilliseconds(902), 0.001);
        Assert.AreEqual(0.8 - 10 * 0.001 / 60, Peak(meter)[0], Tolerance);
    }

    [TestMethod]
    public void ExpiredShortPeak_DoesNotReappearAfterRenderingPause()
    {
        var meter = new MeterBar();
        meter.SetLevelDb(0, -100, Now);
        meter.SetLevelDb(-100, -100, Now.AddMilliseconds(1));

        Assert.IsFalse(NeedsStep(meter, Now.AddSeconds(1)));
        Assert.IsFalse(Step(meter, Now.AddSeconds(1), 0.016));
        CollectionAssert.AreEqual(new double[] { 0, 0 }, Peak(meter));
    }

    [TestMethod]
    public void ExpiredPendingPeak_DoesNotHideNewerLowerPeak()
    {
        var meter = new MeterBar();
        meter.SetLevelDb(0, -100, Now);
        meter.SetLevelDb(-100, -100, Now.AddMilliseconds(1));
        meter.SetLevelDb(-18, -100, Now.AddSeconds(1));
        meter.SetLevelDb(-100, -100, Now.AddSeconds(1).AddMilliseconds(1));

        Step(meter, Now.AddSeconds(1).AddMilliseconds(2), 0.016);

        Assert.AreEqual(0.7, Peak(meter)[0], Tolerance);
        Assert.AreEqual(Now.AddSeconds(1).AddMilliseconds(901), Hold(meter)[0]);
    }

    [TestMethod]
    public void StablePeakAfterIdle_HoldsFor900MillisecondsFromDrop()
    {
        var meter = new MeterBar();
        meter.SetLevelDb(-12, -100, Now);
        for (int frame = 1; frame <= 15; frame++)
            Step(meter, Now.AddMilliseconds(20 * frame), 0.020);
        Assert.IsFalse(Step(meter, Now.AddMilliseconds(320), 0.020));

        var drop = Now.AddSeconds(5);
        meter.SetLevelDb(-100, -100, drop);
        Step(meter, drop, 0.016);
        Assert.AreEqual(drop.AddMilliseconds(900), Hold(meter)[0]);
        Step(meter, drop.AddMilliseconds(900), 0.016);
        Assert.AreEqual(0.8, Peak(meter)[0], Tolerance);
    }

    private static MeterBar CreateMeter(float leftDb, float rightDb, double initial)
    {
        var meter = new MeterBar();
        meter.SetLevelDb(leftDb, rightDb, Now);
        Display(meter)[0] = Display(meter)[1] = initial;
        Hold(meter)[0] = Hold(meter)[1] = Now.AddSeconds(1);
        return meter;
    }

    private static bool Step(MeterBar meter, DateTime now, double dt) =>
        (bool)Method("Step").Invoke(meter, [now, dt])!;

    private static bool NeedsStep(MeterBar meter, DateTime now) =>
        (bool)Method("NeedsStep").Invoke(meter, [now])!;

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

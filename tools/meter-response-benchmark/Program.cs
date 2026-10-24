using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class BenchmarkClock
{
    public static DateTime UtcNow = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
}

internal static class Program
{
    private const int MeterCount = 5;
    private const int Rounds = 7;
    private const double SampleSeconds = 0.087;
    private const int WarmupSeconds = 240;
    private const int DurationSeconds = 2400;
    private static readonly DateTime Epoch = BenchmarkClock.UtcNow;
    private static readonly Size MeterSize = new(38, 300);

    [STAThread]
    private static void Main()
    {
        // No Window is created and no CompositionTarget event loop is started.
        var before = Enumerable.Range(0, MeterCount).Select(_ => new Surface(new Baseline.MeterBar())).ToArray();
        var after = Enumerable.Range(0, MeterCount).Select(_ => new Surface(new SonicCube.Panel.Controls.MeterBar())).ToArray();
        var comparisons = ComparePixels(before[0], after[0]);
        foreach (var surface in before.Concat(after)) surface.Resize(MeterSize, 1.0);

        var measurements = new List<Measurement>();
        foreach (string profile in new[] { "variedEnvelope", "rising6DbStaircase" })
        foreach (int fps in new[] { 60, 144, 240 })
        {
            var frames = MakeFrames(fps, DurationSeconds);
            var warmup = MakeFrames(fps, WarmupSeconds);
            var samples = MakeSamples(DurationSeconds, profile);
            Measure(before, "before", profile, fps, -1, warmup, samples);
            Measure(after, "after", profile, fps, -1, warmup, samples);
            for (int round = 0; round < Rounds; round++)
            {
                var order = round % 2 == 0
                    ? new[] { ("before", before), ("after", after) }
                    : new[] { ("after", after), ("before", before) };
                foreach (var (name, surfaces) in order)
                    measurements.Add(Measure(surfaces, name, profile, fps, round, frames, samples));
            }
        }

        var summaries = measurements.GroupBy(row => (row.Profile, row.Fps, row.Version)).Select(group => new
        {
            profile = group.Key.Profile,
            fps = group.Key.Fps,
            version = group.Key.Version,
            medianMicrosecondsPerFiveMeterFrame = Median(group.Select(row => row.MicrosecondsPerFrame)),
            minMicrosecondsPerFiveMeterFrame = group.Min(row => row.MicrosecondsPerFrame),
            maxMicrosecondsPerFiveMeterFrame = group.Max(row => row.MicrosecondsPerFrame),
            medianManagedBytesPerFiveMeterFrame = Median(group.Select(row => row.ManagedBytesPerFrame)),
            minManagedBytesPerFiveMeterFrame = group.Min(row => row.ManagedBytesPerFrame),
            maxManagedBytesPerFiveMeterFrame = group.Max(row => row.ManagedBytesPerFrame)
        }).ToArray();
        var latency = new List<object>();
        foreach (int fps in new[] { 60, 144, 240 })
        foreach (float target in new[] { -12f, 0f })
        {
            latency.Add(PulseLatency(new Surface(new Baseline.MeterBar()), "before", fps, target));
            latency.Add(PulseLatency(new Surface(new SonicCube.Panel.Controls.MeterBar()), "after", fps, target));
        }
        var smallStepTrajectories = new List<object>();
        foreach (int fps in new[] { 60, 144, 240 })
        {
            smallStepTrajectories.Add(SmallStepTrajectory(new Surface(new Baseline.MeterBar()), "before", fps));
            smallStepTrajectories.Add(SmallStepTrajectory(new Surface(new SonicCube.Panel.Controls.MeterBar()), "after", fps));
        }

        string json = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTime.UtcNow,
            baseline = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../obj/baseline-revision.txt")),
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            meterCount = MeterCount,
            meterWidth = MeterSize.Width,
            meterHeight = MeterSize.Height,
            sampleIntervalMilliseconds = SampleSeconds * 1000,
            simulatedSecondsPerRound = DurationSeconds,
            simulatedWarmupSeconds = WarmupSeconds,
            tieredCompilation = false,
            rounds = Rounds,
            clock = "Both source copies replace DateTime.UtcNow with the same simulated clock; wall-clock retrieval cost is excluded equally.",
            scope = "Managed SetLevelDb plus retained-visual Step work; no GPU, compositor, dispatcher, transport or audio-to-screen timing measurement.",
            comparisons,
            latency,
            smallStepTrajectories,
            summaries,
            measurements
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "results.json"), json);
        Console.WriteLine(json);
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static List<object> ComparePixels(Surface before, Surface after)
    {
        var rows = new List<object>();
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        foreach (var state in new[] { (0.0, 0.0, 0.0, 0.0), (0.75, 0.5, 0.9, 0.6), (0.98, 0.95, 1.0, 0.95) })
        {
            foreach (Surface surface in new[] { before, after })
            {
                surface.Resize(MeterSize, dpi);
                surface.ExplicitDisplay(state.Item1, state.Item2, state.Item3, state.Item4);
            }
            byte[] a = before.Pixels(MeterSize, dpi), b = after.Pixels(MeterSize, dpi);
            int visiblePixels = Enumerable.Range(0, a.Length / 4).Count(i => a[i * 4 + 3] != 0);
            int differences = a.Zip(b).Count(pair => pair.First != pair.Second);
            if (visiblePixels == 0 || differences != 0)
                throw new Exception($"Pixel comparison failed at DPI {dpi}: visible={visiblePixels}, differences={differences}.");
            rows.Add(new { dpi, displayL = state.Item1, displayR = state.Item2, peakL = state.Item3, peakR = state.Item4, visiblePixels, differentBytes = differences });
        }
        return rows;
    }

    private static Frame[] MakeFrames(int fps, int seconds)
    {
        var frames = new Frame[fps * seconds];
        int previousSample = -1;
        for (int frame = 0; frame < frames.Length; frame++)
        {
            double time = frame / (double)fps;
            int sample = (int)Math.Floor(time / SampleSeconds);
            frames[frame] = new Frame(Epoch.AddSeconds(time), sample, sample != previousSample);
            previousSample = sample;
        }
        return frames;
    }

    private static float[][] MakeSamples(int seconds, string profile)
    {
        int count = (int)Math.Ceiling(seconds / SampleSeconds) + 1;
        var samples = new float[count][];
        for (int sample = 0; sample < count; sample++)
        {
            samples[sample] = new float[MeterCount * 2];
            for (int channel = 0; channel < MeterCount * 2; channel++)
            {
                if (profile == "rising6DbStaircase")
                {
                    // Every 5.22 seconds: -48 through 0 dB in 6 dB steps at
                    // the real 87 ms input cadence, then hold and fully release.
                    int phase = (sample + channel * 3) % 60;
                    samples[sample][channel] = phase <= 8 ? -48 + phase * 6 : phase <= 20 ? 0 : -60;
                    continue;
                }
                double time = sample * SampleSeconds;
                // Deterministic varied audio envelope: gentle modulation plus short,
                // staggered transients; every channel receives the same cadence.
                double bed = -34 + 10 * Math.Sin(time * 2.7 + channel * 0.4);
                double pulse = ((sample + channel * 3) % 19) < 3 ? 18 : 0;
                samples[sample][channel] = (float)Math.Clamp(bed + pulse, -60, 0);
            }
        }
        return samples;
    }

    private static Measurement Measure(Surface[] surfaces, string version, string profile, int fps, int round, Frame[] frames, float[][] samples)
    {
        foreach (var surface in surfaces) surface.Reset();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        double dt = 1.0 / fps;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        foreach (ref readonly Frame frame in frames.AsSpan())
        {
            BenchmarkClock.UtcNow = frame.Now;
            if (frame.HasSample)
            {
                float[] sample = samples[frame.Sample];
                for (int i = 0; i < surfaces.Length; i++) surfaces[i].Set(sample[i * 2], sample[i * 2 + 1]);
            }
            foreach (var surface in surfaces) surface.Step(frame.Now, dt);
        }
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds / frames.Length;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        return new Measurement(profile, fps, version, round, frames.Length, elapsed, bytes / (double)frames.Length);
    }

    private static object PulseLatency(Surface surface, string version, int fps, float target)
    {
        surface.Reset();
        BenchmarkClock.UtcNow = Epoch;
        surface.Set(target, target);
        double normalized = Math.Clamp(1 + target / 60.0, 0, 1);
        int reached95 = 0, reached = 0;
        for (int frame = 1; frame <= fps * 2; frame++)
        {
            BenchmarkClock.UtcNow = Epoch.AddSeconds(frame / (double)fps);
            surface.Step(BenchmarkClock.UtcNow, 1.0 / fps);
            if (reached95 == 0 && surface.Display[0] >= normalized * 0.95) reached95 = frame;
            if (Math.Abs(surface.Display[0] - normalized) <= 1e-8) { reached = frame; break; }
        }
        return new
        {
            version, fps, targetDb = target,
            framesTo95Percent = reached95, millisecondsTo95Percent = reached95 * 1000.0 / fps,
            framesToExactTarget = reached, millisecondsToExactTarget = reached * 1000.0 / fps
        };
    }

    private static object SmallStepTrajectory(Surface surface, string version, int fps)
    {
        const float fromDb = -30, targetDb = -24;
        const double from = 0.5, target = 0.6;
        surface.Reset();
        BenchmarkClock.UtcNow = Epoch;
        surface.Set(fromDb, fromDb);
        surface.Step(Epoch, 1);
        surface.ExplicitDisplay(from, from, from, from);
        surface.Set(targetDb, targetDb);
        var frames = new List<object>();
        int reached95 = 0, reachedExact = 0, changedFramesBeforeNextSample = 0;
        double previous = from;
        for (int frame = 1; frame <= fps * 2; frame++)
        {
            double milliseconds = frame * 1000.0 / fps;
            BenchmarkClock.UtcNow = Epoch.AddSeconds(frame / (double)fps);
            surface.Step(BenchmarkClock.UtcNow, 1.0 / fps);
            double level = surface.Display[0];
            if (reached95 == 0 && level >= from + (target - from) * 0.95) reached95 = frame;
            if (reachedExact == 0 && Math.Abs(level - target) <= 1e-8) reachedExact = frame;
            if (milliseconds <= SampleSeconds * 1000)
            {
                if (level != previous) changedFramesBeforeNextSample++;
                frames.Add(new { frame, milliseconds, displayDb = -60 + level * 60, fractionOfStep = (level - from) / (target - from) });
            }
            previous = level;
            if (reachedExact > 0 && milliseconds >= SampleSeconds * 1000) break;
        }
        return new
        {
            version, fps, fromDb, targetDb,
            nextSampleMilliseconds = SampleSeconds * 1000,
            changedFramesBeforeNextSample,
            framesTo95Percent = reached95, millisecondsTo95Percent = reached95 * 1000.0 / fps,
            framesToExactTarget = reachedExact, millisecondsToExactTarget = reachedExact * 1000.0 / fps,
            frames
        };
    }

    private readonly record struct Frame(DateTime Now, int Sample, bool HasSample);
    private sealed record Measurement(string Profile, int Fps, string Version, int Round, int FrameCount, double MicrosecondsPerFrame, double ManagedBytesPerFrame);

    private sealed class Surface
    {
        private readonly FrameworkElement _control;
        private readonly Action<float, float> _set;
        private readonly Func<DateTime, double, bool> _step;
        private readonly Action<DrawingContext> _render;
        private readonly Action _updateVisuals;
        private readonly double[] _peak;
        private readonly DateTime[] _hold;
        private readonly DrawingVisual _backdrop = new();
        public readonly double[] Display;

        public Surface(FrameworkElement control)
        {
            _control = control;
            Type type = control.GetType();
            _set = type.GetMethod("SetLevelDb")!.CreateDelegate<Action<float, float>>(control);
            _step = Method(type, "Step").CreateDelegate<Func<DateTime, double, bool>>(control);
            _render = Method(type, "OnRender").CreateDelegate<Action<DrawingContext>>(control);
            _updateVisuals = Method(type, "UpdateDynamicVisuals").CreateDelegate<Action>(control);
            Display = Field<double[]>(type, "_display");
            _peak = Field<double[]>(type, "_peak");
            _hold = Field<DateTime[]>(type, "_peakHoldUntil");
            control.Resources["FontMono"] = new FontFamily("Consolas");
            foreach (string key in new[] { "BrushMeterTrack", "BrushTickMajor", "BrushThumbLine", "BrushWarn", "BrushTextTertiary", "BrushTextSecondary", "BrushBorderLight" })
                control.Resources[key] = key == "BrushMeterTrack" ? Brushes.DarkSlateGray : key == "BrushWarn" ? Brushes.OrangeRed : Brushes.White;
            control.Resources["ColorMeterGreen"] = Colors.Green;
            control.Resources["ColorMeterYellow"] = Colors.Yellow;
            control.Resources["ColorMeterRed"] = Colors.Red;
            Resize(MeterSize, 1.0);
        }

        private static MethodInfo Method(Type type, string name) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private T Field<T>(Type type, string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_control)!;
        public void Set(float l, float r) => _set(l, r);
        public bool Step(DateTime now, double dt) => _step(now, dt);
        public void Reset()
        {
            BenchmarkClock.UtcNow = Epoch.AddSeconds(-20);
            Set(-100, -100);
            // Consume any retained input peak before directly resetting display.
            for (int i = 0; i < 20; i++) Step(Epoch.AddSeconds(-20 + i), 1);
            ExplicitDisplay(0, 0, 0, 0);
            _hold[0] = _hold[1] = Epoch;
        }
        public void Resize(Size size, double dpi)
        {
            VisualTreeHelper.SetRootDpi(_control, new DpiScale(dpi, dpi));
            _control.Measure(size);
            _control.Arrange(new Rect(size));
            using var dc = _backdrop.RenderOpen();
            _render(dc); // Warm caches once; not part of per-frame work.
        }
        public void ExplicitDisplay(double l, double r, double peakL, double peakR)
        {
            Display[0] = l; Display[1] = r;
            _peak[0] = peakL; _peak[1] = peakR;
            _updateVisuals();
        }
        public byte[] Pixels(Size size, double dpi)
        {
            // Render the real FrameworkElement, including all four retained children.
            _control.InvalidateVisual();
            _control.UpdateLayout();
            int width = (int)(size.Width * dpi), height = (int)(size.Height * dpi);
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
            bitmap.Render(_control);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }
    }
}

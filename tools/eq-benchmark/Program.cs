using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    private const int WarmupIterations = 8_000;
    private const int MeasuredIterations = 12_000;
    private const int Rounds = 10;

    [STAThread]
    private static void Main()
    {
        var before = new Surface(new Baseline.GraphicEqControl());
        var after = new Surface(new SonicCube.Panel.Controls.GraphicEqControl());
        var comparisons = new List<object>();
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        foreach (var size in new[] { new Size(320, 180), new Size(600, 240) })
        foreach (int state in Enumerable.Range(0, 3))
        {
            foreach (var item in new[] { before, after })
            {
                item.Resize(size, dpi);
                item.Point(0, 100, state == 0 ? 0 : -6);
                item.Point(1, 1200, state == 0 ? 0 : 8);
                item.Point(2, 7000, state == 0 ? 0 : 5);
                item.Q(state == 2 ? 2.4 : 0.7);
                item.Hover(state == 2 ? 1 : -1);
            }
            byte[] a = before.Pixels(size, dpi), b = after.Pixels(size, dpi);
            int visiblePixels = Enumerable.Range(0, a.Length / 4).Count(i => a[i * 4 + 3] != 0);
            if (visiblePixels == 0) throw new Exception("Empty render cannot validate visual equivalence.");
            int different = a.Zip(b).Count(pair => pair.First != pair.Second);
            comparisons.Add(new { dpi, size.Width, size.Height, state, visiblePixels, differentBytes = different });
            if (different != 0) throw new Exception($"Pixel mismatch at {size}, {dpi}, {state}: {different}");
        }
        before.Resize(new Size(600, 240), 1.5);
        after.Resize(new Size(600, 240), 1.5);
        var rows = new List<object>();
        foreach (string mode in new[] { "unchanged snapshot", "hover", "parameter drag" })
        {
            Measure(before, mode, WarmupIterations);
            Measure(after, mode, WarmupIterations);
            for (int round = 0; round < Rounds; round++)
            {
                foreach (var (name, surface) in round % 2 == 0
                    ? new[] { ("before", before), ("after", after) }
                    : new[] { ("after", after), ("before", before) })
                {
                    var (us, bytes) = Measure(surface, mode, MeasuredIterations);
                    rows.Add(new { mode, round, version = name, microsecondsPerRender = us, managedBytesPerRender = bytes });
                }
            }
        }
        var result = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTime.UtcNow,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            warmupIterations = WarmupIterations,
            measuredIterations = MeasuredIterations,
            rounds = Rounds,
            comparisons,
            measurements = rows
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "results.json"), result);
        Console.WriteLine(result);
    }

    private static (double, double) Measure(Surface item, string mode, int count)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            if (mode == "unchanged snapshot")
            {
                item.Point(0, 100, -6); item.Point(1, 1200, 8); item.Point(2, 7000, 5); item.Q(0.7);
            }
            else if (mode == "hover") item.Hover(i % 4 - 1);
            else item.Point(1, 400 + i % 3000, -10 + i % 20);
            item.Record();
        }
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds / count;
        return (elapsed, (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)count);
    }

    private sealed class Surface
    {
        private readonly FrameworkElement _control;
        private readonly Action<int, double, double> _setPoint;
        private readonly Action<double> _setQ;
        private readonly Action<DrawingContext> _render;
        private readonly FieldInfo _hover;
        private readonly DrawingVisual _drawing = new();

        public Surface(FrameworkElement control)
        {
            _control = control;
            Type type = control.GetType();
            _setPoint = type.GetMethod("SetPoint")!.CreateDelegate<Action<int, double, double>>(control);
            _setQ = type.GetMethod("SetQ")!.CreateDelegate<Action<double>>(control);
            _render = type.GetMethod("OnRender", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action<DrawingContext>>(control);
            _hover = type.GetField("_hoverIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
            control.Resources["FontMono"] = new FontFamily("Consolas");
            foreach (string key in new[] { "BrushMeterTrack", "BrushTextTertiary", "BrushText", "BrushAccent", "BrushPanelDeep", "BrushBorderLight" })
                control.Resources[key] = key == "BrushAccent" ? Brushes.DodgerBlue : key.Contains("Text") ? Brushes.White : Brushes.DarkSlateGray;
            control.Resources["ColorOn"] = Colors.LimeGreen;
            control.Resources["ColorAmber"] = Colors.Gold;
        }
        public void Point(int i, double frequency, double gain) => _setPoint(i, frequency, gain);
        public void Q(double q) => _setQ(q);
        public void Hover(int i) => _hover.SetValue(_control, i);
        public void Resize(Size size, double dpi)
        {
            VisualTreeHelper.SetRootDpi(_control, new DpiScale(dpi, dpi));
            _control.Measure(size); _control.Arrange(new Rect(size));
        }
        public void Record() { using var dc = _drawing.RenderOpen(); _render(dc); }
        public byte[] Pixels(Size size, double dpi)
        {
            Record();
            int width = (int)(size.Width * dpi), height = (int)(size.Height * dpi);
            var bitmap = new RenderTargetBitmap(width, height, dpi * 96, dpi * 96, PixelFormats.Pbgra32);
            bitmap.Render(_drawing);
            var bytes = new byte[width * height * 4];
            bitmap.CopyPixels(bytes, width * 4, 0);
            return bytes;
        }
    }
}

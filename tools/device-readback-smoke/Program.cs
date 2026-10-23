using System.Diagnostics;
using System.Text.Json;
using SonicCube.Shared.Midi;
using SonicCube.Protocol.Commands;

var runs = new List<object>();
for (int pass = 0; pass < 3; pass++)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var session = DeviceSession.OpenFirst();
    var clock = Stopwatch.StartNew();
    await session.HandshakeAsync(deadline.Token);
    double handshakeMs = clock.Elapsed.TotalMilliseconds;
    float originalVolume = session.Preset.VolPot[0];
    int original48V = session.Global.Phantom48V;
    var elapsed = new List<double>();
    for (int i = 0; i < 10; i++)
    {
        // Exercise writes without changing the user's sound: only write the captured original value.
        clock.Restart();
        await session.SendFloatNoConfirmAsync(CommandAddress.WithLocation(CommandAddress.VolumePot, 0), originalVolume, deadline.Token);
        await Task.Delay(70, deadline.Token);
        await session.RequestAsync(CommandAddress.Preset, deadline.Token);
        elapsed.Add(clock.Elapsed.TotalMilliseconds);
        if (Math.Abs(session.Preset.VolPot[0] - originalVolume) >= .05f)
            throw new Exception("Original-value readback mismatch");
    }
    await session.RequestAsync(CommandAddress.Global, deadline.Token);
    if (session.Global.Phantom48V != original48V) throw new Exception("48V changed unexpectedly");
    elapsed.Sort();
    runs.Add(new { pass, handshakeMs, originalVolume, samples = elapsed.Count,
        medianWriteReadbackMs = elapsed[elapsed.Count / 2], maxWriteReadbackMs = elapsed[^1], phantomUnchanged = true });
}
string json = JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "hardware-results.json"), json);
Console.WriteLine(json);

# Meter response verification

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/meter-response-benchmark/Run.ps1`
on Windows with .NET 10. No visible window, MIDI connection or audio input is used.
The pinned baseline is the retained-visual meter before the response fix. The
script generates both control copies under ignored `obj/`; it does not modify
product files. JSON is saved to `bin/Release/net10.0-windows/results.json`.

To compare against a captured source file instead of that Git revision, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/meter-response-benchmark/Run.ps1 -BaselineSourcePath out/meter-smoothing-baseline/MeterBar.cs
```

`-BaselineRevision <commit>` selects a different Git revision when no source path
is supplied. The selected baseline description is included in the result JSON.

The benchmark uses five real WPF meters, each 38 × 300 DIP at 100% DPI, and warms
their static bitmap caches. It invokes compiled `SetLevelDb` and `Step` delegates
with prerecorded input every 87 ms and frame intervals of 60, 144 and 240 Hz.
Each rate has 240 simulated seconds of warmup and seven alternating before/after
rounds of 2,400 simulated seconds. Tiered compilation is disabled for this
harness to avoid mixing startup tiers with steady-state timing. It measures managed time and per-thread
allocation for all five meters per frame. `OnRender` is only used for initial
cache setup: both versions update their retained child visuals during measured
frames, as in the application.

Both generated copies substitute a deterministic clock for `DateTime.UtcNow`,
so input timestamps and simulated animation timestamps stay consistent and peak
hold can expire normally. This equally excludes the clock retrieval cost; all
other control code comes from the selected baseline or current working tree.
Two separately reported input profiles cover smooth variations with staggered
short transients (`variedEnvelope`) and repeated 6 dB rising steps every 87 ms
from −48 to 0 dB, followed by a steady hold and full release
(`rising6DbStaircase`). The staircase cycle lasts 5.22 seconds; channels are
staggered. This second profile exposes the extra drawing work required to spread
small rises across multiple frames rather than completing them in one frame.
The harness reports medians and min/max across rounds; small differences may be
noise. Results do not measure whole-process CPU, GPU, WPF compositor, dispatcher,
IPC latency or audio-to-screen synchronization.

Separate checks report animation time from an empty bar to −12 and 0 dB at each
frame rate. Reaching 95% and reaching the exact target are distinct measurements:
an exponential response has a small tail before it reaches the control's final
snap threshold. A −30 to −24 dB step also records every frame before the next
87 ms device sample, its count of changing frames, and both convergence times.
These direct `Step` measurements do not measure dispatcher wake time.
Nine pixel comparisons cover three explicit display/peak states at
100%, 150% and 200% DPI, verifying drawing equivalence independently of changed
ballistics. These are regression checks, not DPI adaptation changes.

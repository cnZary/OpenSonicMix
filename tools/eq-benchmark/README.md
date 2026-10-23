# Global EQ verification

Run `powershell -NoProfile -ExecutionPolicy Bypass -File tools/eq-benchmark/Run.ps1` on
Windows with the .NET 10 SDK. No MIDI device or visible window is used.
The script extracts the pre-optimization control from the pinned baseline Git
revision into ignored `obj/Baseline.cs`, and compares it with the current source.
Use `-BaselineRevision <commit>` to deliberately compare a different baseline.
The JSON output is also saved to `bin/Release/net10.0-windows/results.json`.

The harness compares 18 pixel renders: 320 × 180 and 600 × 240 DIP surfaces,
100%, 150%, and 200% DPI, with flat EQ, adjusted bands, and a hovered point with
changed Q. These DPI checks verify unchanged output; they do not add DPI support.
A transparent render or any pixel difference fails the run.

The benchmark measures managed `OnRender` drawing-recording time and per-thread
allocation on one 600 × 240 DIP EQ at 150% DPI. Each workload has 8,000 warmup
iterations and ten alternating before/after rounds of 12,000 renders. Workloads
are unchanged snapshots, changing hover, and a changing middle band. Rendering
is forced even when the equality shortcut would normally avoid it.

This does not measure the GPU, WPF compositor, whole-process CPU, or live frame
cadence. Compare medians and round ranges; small timing differences can be noise.

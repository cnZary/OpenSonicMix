param(
    [string]$BaselineRevision = 'f86ae35186ee615eaaff33bb285d9cf25ea6b6df',
    [string]$BaselineSourcePath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
if ($BaselineSourcePath) {
    $resolvedBaseline = (Resolve-Path -LiteralPath $BaselineSourcePath).Path
    $baselineSource = [System.IO.File]::ReadAllText($resolvedBaseline)
    $baselineDescription = "Source file: $resolvedBaseline"
} else {
    $baselineLines = & git -C $repositoryRoot show "${BaselineRevision}:SonicCube.Panel/Controls/MeterBar.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read the requested baseline revision.' }
    $baselineSource = $baselineLines -join "`n"
    $baselineDescription = "Git revision: $BaselineRevision"
}
$baselineSource = $baselineSource.Replace(
    'namespace SonicCube.Panel.Controls;',
    "using SonicCube.Panel.Controls;`nnamespace Baseline;")
$currentSource = [System.IO.File]::ReadAllText((Join-Path $repositoryRoot 'SonicCube.Panel/Controls/MeterBar.cs'))
$generatedDirectory = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
# Both copies use the same deterministic source/animation clock. This keeps the
# 900 ms peak hold meaningful while simulating minutes without real-time waits.
[System.IO.File]::WriteAllText((Join-Path $generatedDirectory 'Baseline.cs'), $baselineSource.Replace('DateTime.UtcNow', 'BenchmarkClock.UtcNow'))
[System.IO.File]::WriteAllText((Join-Path $generatedDirectory 'Current.cs'), $currentSource.Replace('DateTime.UtcNow', 'BenchmarkClock.UtcNow'))
[System.IO.File]::WriteAllText((Join-Path $generatedDirectory 'baseline-revision.txt'), $baselineDescription)
& dotnet run --project (Join-Path $PSScriptRoot 'MeterResponseBench.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Meter response benchmark failed.' }

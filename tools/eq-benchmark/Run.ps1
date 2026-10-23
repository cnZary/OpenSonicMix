param(
    [string]$BaselineRevision = 'fd8fbfa6d6f9440e133a52b738a88300c7bfcb8d'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$baselineSource = & git -C $repositoryRoot show "${BaselineRevision}:SonicCube.Panel/Controls/GraphicEqControl.cs"
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the requested baseline revision.' }
$baselineSource = ($baselineSource -join "`n").Replace(
    'namespace SonicCube.Panel.Controls;',
    "using SonicCube.Panel.Controls;`nnamespace Baseline;")
$generatedDirectory = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Path $generatedDirectory -Force | Out-Null
[System.IO.File]::WriteAllText((Join-Path $generatedDirectory 'Baseline.cs'), $baselineSource)
& dotnet run --project (Join-Path $PSScriptRoot 'EqBench.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'EQ benchmark failed.' }

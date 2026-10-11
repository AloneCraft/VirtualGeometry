# Runs the planet validations one at a time in batchmode and writes a one-line verdict per run.
#
# Usage (from the project root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-PlanetRegression.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-PlanetRegression.ps1 -Jobs MoonBall,BodyStack -Modes on -Tests
# (-Jobs and -Modes accept a comma separated list, also when the script is started with -File.)
#
# With -HashBaseline <file>, the RealMoon run in mode "on" also compares its vertex hashes against that recorded file.
#
# Modes (how the height-shift threshold is overridden, see VirtualGeometry.Omni.Editor.HeightShiftOverride):
#   on     default behaviour (no override)
#   forced -heightShiftThreshold 0   lift every cell that has a non-zero height (finds readers that forgot SphereOrigin)
#   off    -heightShiftThreshold 1e9 never lift (reproduces the behaviour before the cell-origin lift). RealMoon, SyntheticRelief, RealMoonBall and SyntheticMoonBall are EXPECTED to fail here.
# Each run copies Report.txt to Report.<mode>.txt in the job's result folder and appends a line to
# TestResults\Planet\Regression\<Tag>-summary.txt. Exit code 1 if any run did not give the expected verdict,
# if a job name or mode is unknown, or if nothing was run.
param(
    [string[]]$Jobs = @(),
    [string[]]$Modes = @(),
    [string]$Tag = "regression",
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe",
    [switch]$Tests,
    # A recorded vertex-hash baseline file. When given, the RealMoon run in mode "on" compares its vertex hashes against it
    # (-vertexHashMode compare). The baseline is per machine/GPU/driver, so it is not kept in the repository; a different environment is reported as "not compared".
    [string]$HashBaseline = "",
    # Extra Unity arguments for every run, as one space separated string (for example "-poolCapacity 2048").
    [string]$Extra = ""
)

$ErrorActionPreference = "Stop"
# powershell -File passes "a,b" as one string, so split every element on commas.
$Jobs = @($Jobs | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })
$Modes = @($Modes | ForEach-Object { $_ -split "," } | Where-Object { $_ -ne "" })
$project = Split-Path -Parent $PSScriptRoot
$prefix = "VirtualGeometry.Omni.Editor."
# name, method, result folder, modes that apply
$table = @(
    @("P2", "P2OmniPlanetValidation.Run", "P2", @("on", "forced")),
    @("P2Large", "P2OmniPlanetValidation.RunLarge", "P2Large", @("on", "forced")),
    @("P4b", "P4bDescentValidation.Run", "P4b", @("on", "forced")),
    @("P4bEarth", "P4bDescentValidation.RunEarth", "P4bEarth", @("on", "forced")),
    @("P5a", "P5aOceanValidation.Run", "P5a", @("on", "forced")),
    @("MoonBall", "MoonBallValidation.Run", "MoonBall", @("on", "forced")),
    @("GiantBall", "MoonBallValidation.RunGiant", "GiantBall", @("on", "forced")),
    @("BodyStack", "BodyStackValidation.Run", "BodyStack", @("on", "forced")),
    @("BodyStackCells", "BodyStackValidation.RunCells", "BodyStackCells", @("on", "forced")),
    @("RealMoonBall", "MoonBallValidation.RunRealMoon", "RealMoonBall", @("on", "off")),
    @("SyntheticMoonBall", "MoonBallValidation.RunSyntheticMoon", "SyntheticMoonBall", @("on", "off")),
    @("ReliefBall", "MoonBallValidation.RunReliefBall", "ReliefBall", @("on")),
    @("DepthResolution", "DepthResolutionValidation.Run", "DepthResolution", @("on")),
    @("CameraStateRelease", "CameraStateReleaseValidation.Run", "CameraStateRelease", @("on")),
    @("PlayStopLeak", "PlayStopLeakValidation.Run", "PlayStopLeak", @("on")),
    @("Amplification", "AmplificationValidation.Run", "Amplification", @("on")),
    @("ColorLayer", "ColorLayerValidation.Run", "ColorLayer", @("on")),
    @("OceanProbe", "OceanProbeValidation.Run", "OceanProbe", @("on")),
    @("ObserverCamera", "MoonBallValidation.RunObserver", "ObserverCamera", @("on")),
    @("ViewScale", "MoonBallValidation.RunViewScale", "ViewScale", @("on")),
    @("RealMoon", "RealMoonValidation.Run", "RealMoon", @("on", "off")),
    @("SyntheticRelief", "RealMoonValidation.RunSynthetic", "SyntheticRelief", @("on", "off")),
    @("RealMoonPolar", "RealMoonValidation.RunPolar", "RealMoonPolar", @("on")),
    @("SyntheticPolar", "RealMoonValidation.RunPolarSynthetic", "SyntheticPolar", @("on")),
    @("PlaneSink", "PlaneSinkValidation.Run", "PlaneSink", @("on")),
    @("PlaneSinkPlay", "PlaneSinkPlaySmoke.Run", "PlaneSinkPlay", @("on"))
)
$knownJobs = $table | ForEach-Object { $_[0] }
$knownModes = @("on", "forced", "off")
if ($Jobs.Count -eq 0) { $Jobs = $knownJobs }
foreach ($name in $Jobs) { if ($knownJobs -notcontains $name) { Write-Error "Unknown job '$name'. Known: $($knownJobs -join ', ')" } }
foreach ($name in $Modes) { if ($knownModes -notcontains $name) { Write-Error "Unknown mode '$name'. Known: $($knownModes -join ', ')" } }

function ArgumentString([string[]]$items) {
    # Start-Process joins an argument array with spaces and adds no quotes, so quote every item that has a space.
    return ($items | ForEach-Object { if ($_ -match "\s") { '"' + $_ + '"' } else { $_ } }) -join " "
}

$logs = Join-Path $project "TestResults\Planet\Regression"
New-Item -ItemType Directory -Force $logs | Out-Null
$summary = Join-Path $logs "$Tag-summary.txt"
Set-Content -Path $summary -Value "regression $Tag" -Encoding utf8
$bad = 0
$ran = 0

if ($Tests) {
    $xml = Join-Path $logs "$Tag-tests.xml"
    $testArguments = @("-batchmode", "-force-d3d12", "-projectPath", $project, "-runTests", "-testPlatform", "EditMode",
        "-assemblyNames", "VirtualGeometry.World.Tests.Editor;VirtualGeometry.Omni.Tests.Editor", "-testResults", $xml, "-logFile", (Join-Path $logs "$Tag-tests.log"))
    $p = Start-Process -FilePath $Unity -ArgumentList (ArgumentString $testArguments) -PassThru -Wait
    $line = "EditMode tests exit=$($p.ExitCode)"
    if (Test-Path $xml) {
        $head = (Get-Content $xml -TotalCount 3 -Encoding utf8) -join " "
        if ($head -match 'total="(\d+)" passed="(\d+)" failed="(\d+)"') { $line += " total=$($Matches[1]) passed=$($Matches[2]) failed=$($Matches[3])" }
    }
    if ($p.ExitCode -ne 0) { $bad++ }
    $ran++
    Add-Content -Path $summary -Value $line -Encoding utf8
}

foreach ($row in $table) {
    if ($Jobs -notcontains $row[0]) { continue }
    $dir = Join-Path $project ("TestResults\Planet\" + $row[2])
    foreach ($mode in $row[3]) {
        if ($Modes.Count -gt 0 -and $Modes -notcontains $mode) { continue }
        $arguments = @("-batchmode", "-force-d3d12", "-projectPath", $project, "-executeMethod", ($prefix + $row[1]), "-logFile", (Join-Path $logs ($row[0] + "-" + $mode + ".log")))
        if ($mode -eq "forced") { $arguments += @("-heightShiftThreshold", "0") }
        if ($mode -eq "off") { $arguments += @("-heightShiftThreshold", "1e9") }
        if ($Extra -ne "") { $arguments += @($Extra -split " " | Where-Object { $_ -ne "" }) }
        if ($HashBaseline -ne "" -and $row[0] -eq "RealMoon" -and $mode -eq "on") { $arguments += @("-vertexHashBaseline", $HashBaseline, "-vertexHashMode", "compare") }
        $report = Join-Path $dir "Report.txt"
        if (Test-Path $report) { Remove-Item $report -Force }
        $p = Start-Process -FilePath $Unity -ArgumentList (ArgumentString $arguments) -PassThru -Wait
        $ran++
        $verdict = "(no report)"
        if (Test-Path $report) {
            Copy-Item $report (Join-Path $dir ("Report." + $mode + ".txt")) -Force
            $verdict = (Get-Content $report -Encoding utf8 | Where-Object { $_ -eq "PASSED" -or $_ -eq "FAILED" } | Select-Object -First 1)
            if (-not $verdict) { $verdict = "(no verdict)" }
        }
        $expected = "PASSED"
        if ($mode -eq "off") { $expected = "FAILED" }
        $state = "ok"
        if ($verdict -ne $expected) { $state = "NG"; $bad++ }
        Add-Content -Path $summary -Value ("{0} {1} exit={2} verdict={3} expected={4} {5}" -f $row[0], $mode, $p.ExitCode, $verdict, $expected, $state) -Encoding utf8
    }
}

if ($ran -eq 0) {
    Add-Content -Path $summary -Value "NOTHING WAS RUN (no job and mode matched)" -Encoding utf8
    exit 1
}
Add-Content -Path $summary -Value ("done ran=" + $ran + " bad=" + $bad) -Encoding utf8
if ($bad -gt 0) { exit 1 }
exit 0

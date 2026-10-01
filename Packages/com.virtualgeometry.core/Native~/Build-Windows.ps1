[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration='Release'
)

$ErrorActionPreference='Stop'
$nativeRoot=[IO.Path]::GetFullPath($PSScriptRoot)
$packageRoot=[IO.Path]::GetFullPath((Join-Path $nativeRoot '..'))
$projectRoot=[IO.Path]::GetFullPath((Join-Path $packageRoot '..\..'))
$buildRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot 'Library\VirtualGeometryNative'))
$outputRoot=[IO.Path]::GetFullPath((Join-Path $packageRoot 'Editor\Plugins\x86_64'))

$cmakeCandidates=@(
    'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe',
    'C:\Program Files\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
)
$cmake=$cmakeCandidates | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
if(-not $cmake){
    $cmakeCommand=Get-Command cmake -ErrorAction SilentlyContinue
    if($cmakeCommand){$cmake=$cmakeCommand.Source}
}
if(-not $cmake){throw 'CMake was not found. Install the Visual Studio C++ and CMake workloads.'}

New-Item -ItemType Directory -Force -Path $buildRoot,$outputRoot | Out-Null
& $cmake -S $nativeRoot -B $buildRoot -G 'Visual Studio 18 2026' -A x64 "-DVG_OUTPUT_DIR=$($outputRoot.Replace('\','/'))"
if($LASTEXITCODE -ne 0){throw "CMake configure failed with exit code $LASTEXITCODE."}
& $cmake --build $buildRoot --config $Configuration --target VirtualGeometryMeshOptimizer
if($LASTEXITCODE -ne 0){throw "Native build failed with exit code $LASTEXITCODE."}

$plugin=Join-Path $outputRoot 'VirtualGeometryMeshOptimizer.dll'
if(-not (Test-Path -LiteralPath $plugin)){throw "Native build did not produce $plugin."}
Write-Output $plugin


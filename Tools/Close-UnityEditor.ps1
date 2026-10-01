[CmdletBinding()]
param(
    [string]$ProjectPath='',
    [int]$TimeoutSeconds=120
)
# Closes the interactive Unity Editor that has this project open, without losing work.
# Batchmode validation cannot run while the same project is open in an Editor, so run this first.
# 1. Find Unity.exe whose command line opens this project and is not -batchmode. Nothing to do if none.
# 2. Write Temp/CloseEditorRequest. Assets/Editor/EditorCloseRequest.cs leaves play mode, saves dirty scenes/assets and exits.
#    If it reports work that cannot be saved automatically (untitled scene, unsaved prefab edit), stop and report it.
# 3. If the Editor does not answer, send a window close request (Unity then shows its own save prompt).
# Never kills the Editor. Exit code 0 when no Editor has the project open anymore, 1 otherwise.
$ErrorActionPreference='Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so resolve the default here.
if($ProjectPath -eq ''){$ProjectPath=Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)}
$project=[System.IO.Path]::GetFullPath($ProjectPath).TrimEnd('\')
$pattern='(?i)-projectpath\s+"?'+[regex]::Escape($project)+'\\?"?(\s|$)'

function Get-ProjectUnity([bool]$interactiveOnly){
    $found=@()
    foreach($process in @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'")){
        $line=$process.CommandLine
        if($null -eq $line){continue}
        $line=$line.Replace('/','\')
        if($line -notmatch $pattern){continue}
        if($interactiveOnly -and $line -match '(?i)-batchmode'){continue}
        $found+=$process
    }
    return ,$found
}

function Wait-Closed([int]$seconds,[string]$result){
    $deadline=(Get-Date).AddSeconds($seconds)
    while((Get-Date) -lt $deadline){
        if((Get-ProjectUnity $true).Count -eq 0){return 'closed'}
        if($result -ne '' -and (Test-Path $result)){
            # The first line is an ASCII reason code; the second line is the Japanese detail shown in the Unity Console.
            $first=([System.IO.File]::ReadAllText($result,[System.Text.Encoding]::UTF8) -split "`n")[0].Trim()
            if($first.StartsWith('NOT CLOSED')){return $first}
        }
        Start-Sleep -Milliseconds 500
    }
    return 'timeout'
}

$editors=Get-ProjectUnity $true
if($editors.Count -eq 0){
    Write-Output "No interactive Unity Editor has $project open."
    exit 0
}
$ids=($editors|ForEach-Object{$_.ProcessId}) -join ','
Write-Output "Closing Unity Editor (PID $ids) for $project"
$temp=Join-Path $project 'Temp'
$request=Join-Path $temp 'CloseEditorRequest'
$result=Join-Path $temp 'CloseEditorResult.txt'
if(Test-Path $result){Remove-Item $result}
New-Item -ItemType Directory -Force $temp|Out-Null
[System.IO.File]::WriteAllText($request,'close')

$state=Wait-Closed $TimeoutSeconds $result
if($state.StartsWith('NOT CLOSED')){
    $messages=@{
        'untitled-scene'='an untitled scene has unsaved changes';
        'prefab-stage'='the prefab being edited has unsaved changes';
        'save-failed'='a modified scene could not be saved'
    }
    $code=$state.Substring('NOT CLOSED'.Length).Trim()
    $message=$messages[$code]
    if($null -eq $message){$message=$code}
    Write-Output "Unity Editor was left open because $message. Save or discard it in the Editor (details in the Unity Console), then run this again."
    exit 1
}
if($state -eq 'timeout'){
    # The Editor did not answer (old domain without the request handler, modal dialog, ...). Ask the window to close.
    if(Test-Path $request){Remove-Item $request}
    Write-Output "No answer to the close request. Sending a window close request."
    foreach($editor in Get-ProjectUnity $true){
        $process=Get-Process -Id $editor.ProcessId -ErrorAction SilentlyContinue
        if($null -ne $process){[void]$process.CloseMainWindow()}
    }
    $state=Wait-Closed $TimeoutSeconds ''
    if($state -ne 'closed'){
        Write-Output "Unity Editor did not close (a save prompt may be waiting). Close it manually."
        exit 1
    }
}
# Asset import workers of the closed Editor exit right after it. Wait so batchmode does not see the project as open.
$deadline=(Get-Date).AddSeconds(30)
while((Get-ProjectUnity $false).Count -gt 0 -and (Get-Date) -lt $deadline){Start-Sleep -Milliseconds 500}
Write-Output "Unity Editor closed."
exit 0

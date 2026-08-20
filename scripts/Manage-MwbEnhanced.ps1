[CmdletBinding()]
param(
    [ValidateSet('status', 'start', 'stop', 'restart')]
    [string]$Action = 'status',

    [string]$RemoteHost = 'pc-b',

    [string]$LocalInstallDir = 'C:\AgentWork\mwb-enhanced',

    [string]$RemoteInstallDir = 'C:\AgentWork\mwb-enhanced',

    [string]$RemoteInteractiveUser = 'ID-BLUEBERRY\12298'
)

$ErrorActionPreference = 'Stop'
$mainName = 'PowerToys.MouseWithoutBorders.exe'
$helperName = 'PowerToys.MouseWithoutBordersHelper.exe'
$localMain = Join-Path $LocalInstallDir $mainName
$localHelper = Join-Path $LocalInstallDir $helperName
$remoteMain = Join-Path $RemoteInstallDir $mainName

function Invoke-RemoteScript {
    param(
        [Parameter(Mandatory)] [string]$Script,
        [hashtable]$Variables = @{}
    )

    $body = @(
        '$ErrorActionPreference = ''Stop''' 
        '$ProgressPreference = ''SilentlyContinue''' 
        foreach ($entry in $Variables.GetEnumerator()) {
            $escaped = "'" + ([string]$entry.Value -replace "'", "''") + "'"
            "`$$($entry.Key) = $escaped"
        }
        $Script
    ) -join [Environment]::NewLine
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    & ssh $RemoteHost "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) {
        throw "Remote command failed on $RemoteHost (exit code $LASTEXITCODE)."
    }
}

function Stop-MwbLocal {
    param([string]$MainPath, [string]$HelperPath)

    $paths = @($MainPath, $HelperPath) | ForEach-Object { [IO.Path]::GetFullPath($_) }
    Get-CimInstance Win32_Process |
        Where-Object { $_.ExecutablePath -and $paths -contains ([IO.Path]::GetFullPath($_.ExecutablePath)) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Start-MwbLocal {
    if (-not (Test-Path -LiteralPath $localMain)) {
        throw "Local MWB binary was not found: $localMain"
    }

    Stop-MwbLocal -MainPath $localMain -HelperPath $localHelper
    $process = Start-Process -FilePath $localMain -WorkingDirectory $LocalInstallDir -PassThru
    Start-Sleep -Seconds 2
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
    if (-not $current -or $current.SessionId -eq 0) {
        Stop-MwbLocal -MainPath $localMain -HelperPath $localHelper
        throw "Local MWB did not start in an interactive session. PID=$($process.Id), Session=$($current.SessionId)"
    }
    Write-Output ("LOCAL STARTED PID={0} SESSION={1}" -f $process.Id, $current.SessionId)
}

function Invoke-RemoteStop {
    $script = @'
$main = [IO.Path]::GetFullPath($RemoteMain)
$helper = [IO.Path]::GetFullPath((Join-Path $RemoteDir 'PowerToys.MouseWithoutBordersHelper.exe'))
Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -in @($main, $helper) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Write-Output 'REMOTE STOPPED'
'@
    Invoke-RemoteScript -Script $script -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir }
}

function Invoke-RemoteStart {
    Invoke-RemoteStop
    $script = @'
if (-not (Test-Path -LiteralPath $RemoteMain)) { throw "Remote MWB binary was not found: $RemoteMain" }
$taskName = 'MwbEnhancedOneClick-' + [Guid]::NewGuid().ToString('N')
$action = New-ScheduledTaskAction -Execute $RemoteMain -WorkingDirectory $RemoteDir
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 3
    $process = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq [IO.Path]::GetFullPath($RemoteMain) } | Select-Object -First 1
    if (-not $process) { throw 'Remote MWB did not start.' }
    if ($process.SessionId -eq 0) { throw "Remote MWB started in Session 0 (PID=$($process.ProcessId))." }
    Write-Output ("REMOTE STARTED PID={0} SESSION={1} USER={2}" -f $process.ProcessId, $process.SessionId, $InteractiveUser)
}
finally {
    schtasks.exe /Delete /TN $taskName /F | Out-Null
}
'@
    Invoke-RemoteScript -Script $script -Variables @{
        RemoteMain = $remoteMain
        RemoteDir = $RemoteInstallDir
        InteractiveUser = $RemoteInteractiveUser
    }
}

function Show-MwbStatus {
    Write-Output '--- LOCAL ---'
    Get-CimInstance Win32_Process |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath -in @($localMain, $localHelper) } |
        Select-Object ProcessId, SessionId, ExecutablePath, CommandLine |
        Format-Table -AutoSize

    Write-Output "--- $RemoteHost ---"
    $script = @'
Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -in @($RemoteMain, (Join-Path $RemoteDir 'PowerToys.MouseWithoutBordersHelper.exe')) } |
    Select-Object ProcessId, SessionId, ExecutablePath, CommandLine |
    Format-Table -AutoSize
'@
    Invoke-RemoteScript -Script $script -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir }
}

switch ($Action) {
    'status'  { Show-MwbStatus }
    'stop'    { Stop-MwbLocal -MainPath $localMain -HelperPath $localHelper; Invoke-RemoteStop }
    'start'   { Start-MwbLocal; Invoke-RemoteStart }
    'restart' {
        Stop-MwbLocal -MainPath $localMain -HelperPath $localHelper
        Invoke-RemoteStop
        Start-Sleep -Seconds 2
        Start-MwbLocal
        Invoke-RemoteStart
    }
}

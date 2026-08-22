[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'restart', 'status', 'install-autostart', 'remove-autostart')]
    [string]$Action = 'start',

    [string]$RemoteHost = 'pc-b',
    [string]$LocalInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInteractiveUser = 'ID-BLUEBERRY\12298',
    [string]$TaskName = 'MWBEnhancedAutoStart'
)

$ErrorActionPreference = 'Stop'
$mainName = 'PowerToys.MouseWithoutBorders.exe'
$helperName = 'PowerToys.MouseWithoutBordersHelper.exe'
$localMain = Join-Path $LocalInstallDir $mainName
$localHelper = Join-Path $LocalInstallDir $helperName
$remoteMain = Join-Path $RemoteInstallDir $mainName

function Invoke-RemoteScript {
    param([Parameter(Mandatory)][string]$Script, [hashtable]$Variables = @{})

    $body = @('$ErrorActionPreference = ''Stop''', '$ProgressPreference = ''SilentlyContinue''')
    foreach ($entry in $Variables.GetEnumerator()) {
        $escaped = "'" + ([string]$entry.Value -replace "'", "''") + "'"
        $body += "`$$($entry.Key) = $escaped"
    }
    $body += $Script
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(($body -join [Environment]::NewLine)))
    & ssh $RemoteHost "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded"
    if ($LASTEXITCODE -ne 0) { throw "Remote command failed on $RemoteHost (exit code $LASTEXITCODE)." }
}

function Get-ProcessPaths { @([IO.Path]::GetFullPath($localMain), [IO.Path]::GetFullPath($localHelper)) }

function Stop-LocalMwb {
    $paths = Get-ProcessPaths
    Get-CimInstance Win32_Process |
        Where-Object { $_.ExecutablePath -and $paths -contains ([IO.Path]::GetFullPath($_.ExecutablePath)) } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    $enhancedPath = [IO.Path]::GetFullPath($localMain)
    $portOwners = Get-NetTCPConnection -LocalPort 15101,15102 -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($ownerPid in $portOwners) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerPid" -ErrorAction SilentlyContinue
        if ($process -and $process.Name -in @('MouseWithoutBorders.exe', 'PowerToys.MouseWithoutBorders.exe') -and $process.ExecutablePath -ne $enhancedPath) {
            Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-LocalMwb {
    if (-not (Test-Path -LiteralPath $localMain)) { throw "Local MWB binary not found: $localMain" }
    Stop-LocalMwb
    $process = Start-Process -FilePath $localMain -WorkingDirectory $LocalInstallDir -PassThru
    Start-Sleep -Seconds 2
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
    if (-not $current -or $current.SessionId -eq 0) {
        Stop-LocalMwb
        throw "Local MWB did not start in an interactive session. PID=$($process.Id)"
    }
    Write-Output ("LOCAL STARTED PID={0} SESSION={1}" -f $process.Id, $current.SessionId)
}

function Invoke-RemoteStop {
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir } -Script @'
$helper = Join-Path $RemoteDir 'PowerToys.MouseWithoutBordersHelper.exe'
Get-CimInstance Win32_Process |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -in @([IO.Path]::GetFullPath($RemoteMain), [IO.Path]::GetFullPath($helper)) } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$enhancedPath = [IO.Path]::GetFullPath($RemoteMain)
$portOwners = Get-NetTCPConnection -LocalPort 15101,15102 -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique
foreach ($ownerPid in $portOwners) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId = $ownerPid" -ErrorAction SilentlyContinue
    if ($process -and $process.Name -in @('MouseWithoutBorders.exe', 'PowerToys.MouseWithoutBorders.exe') -and $process.ExecutablePath -ne $enhancedPath) {
        Stop-Process -Id $ownerPid -Force -ErrorAction SilentlyContinue
    }
}
Write-Output 'REMOTE STOPPED'
'@
}

function Invoke-RemoteStart {
    Invoke-RemoteStop
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir; InteractiveUser = $RemoteInteractiveUser } -Script @'
if (-not (Test-Path -LiteralPath $RemoteMain)) { throw "Remote MWB binary not found: $RemoteMain" }
$taskName = 'MwbEnhancedOneClick-' + [Guid]::NewGuid().ToString('N')
$action = New-ScheduledTaskAction -Execute $RemoteMain -WorkingDirectory $RemoteDir
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 3
    $current = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq [IO.Path]::GetFullPath($RemoteMain) } | Select-Object -First 1
    if (-not $current) { throw 'Remote MWB did not start.' }
    if ($current.SessionId -eq 0) { throw "Remote MWB started in Session 0 (PID=$($current.ProcessId))." }
    Write-Output ("REMOTE STARTED PID={0} SESSION={1} USER={2}" -f $current.ProcessId, $current.SessionId, $InteractiveUser)
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}
'@
}

function Install-LocalAutostart {
    if (-not (Test-Path -LiteralPath $localMain)) { throw "Local MWB binary not found: $localMain" }
    $principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
    $action = New-ScheduledTaskAction -Execute $localMain -WorkingDirectory $LocalInstallDir
    Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Force | Out-Null
    Write-Output "LOCAL AUTOSTART INSTALLED: $TaskName"
}

function Install-RemoteAutostart {
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir; InteractiveUser = $RemoteInteractiveUser; Task = $TaskName } -Script @'
if (-not (Test-Path -LiteralPath $RemoteMain)) { throw "Remote MWB binary not found: $RemoteMain" }
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Highest
$action = New-ScheduledTaskAction -Execute $RemoteMain -WorkingDirectory $RemoteDir
Register-ScheduledTask -TaskName $Task -Action $action -Principal $principal -Force | Out-Null
Write-Output "REMOTE AUTOSTART INSTALLED: $Task"
'@
}

function Remove-LocalAutostart {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "LOCAL AUTOSTART REMOVED: $TaskName"
}

function Remove-RemoteAutostart {
    Invoke-RemoteScript -Variables @{ Task = $TaskName } -Script @'
Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "REMOTE AUTOSTART REMOVED: $Task"
'@
}

function Show-Status {
    Write-Output '--- LOCAL / 本机 ---'
    $paths = Get-ProcessPaths
    Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $paths -contains ([IO.Path]::GetFullPath($_.ExecutablePath)) } |
        Select-Object ProcessId, SessionId, ExecutablePath | Format-Table -AutoSize
    Write-Output "--- $RemoteHost / 远端 ---"
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteDir = $RemoteInstallDir; Task = $TaskName } -Script @'
$helper = Join-Path $RemoteDir 'PowerToys.MouseWithoutBordersHelper.exe'
Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and $_.ExecutablePath -in @([IO.Path]::GetFullPath($RemoteMain), [IO.Path]::GetFullPath($helper)) } |
    Select-Object ProcessId, SessionId, ExecutablePath | Format-Table -AutoSize
$taskInfo = Get-ScheduledTask -TaskName $Task -ErrorAction SilentlyContinue
if ($taskInfo) { $taskInfo | Select-Object TaskName, State | Format-Table -AutoSize }
exit 0
'@
    Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | Select-Object TaskName, State | Format-Table -AutoSize
}

switch ($Action) {
    'start' { Start-LocalMwb; Invoke-RemoteStart }
    'stop' { Stop-LocalMwb; Invoke-RemoteStop }
    'restart' { Stop-LocalMwb; Invoke-RemoteStop; Start-Sleep -Seconds 2; Start-LocalMwb; Invoke-RemoteStart }
    'status' { Show-Status }
    'install-autostart' { Install-LocalAutostart; Install-RemoteAutostart }
    'remove-autostart' { Remove-LocalAutostart; Remove-RemoteAutostart }
}

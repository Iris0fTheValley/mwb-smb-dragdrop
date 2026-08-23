[CmdletBinding()]
param(
    [ValidateSet('start', 'stop', 'restart', 'status', 'install-autostart', 'remove-autostart')]
    [string]$Action = 'start',

    [string]$RemoteHost = 'pc-b',
    [string]$LocalInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInteractiveUser = 'ID-BLUEBERRY\12298',
    [string]$RemoteUserProfile = '',
    [string]$TaskName = 'MWBEnhancedAutoStart',
    [int]$TcpPort = 15101
)

$ErrorActionPreference = 'Stop'
$mainName = 'PowerToys.MouseWithoutBorders.exe'
$helperName = 'PowerToys.MouseWithoutBordersHelper.exe'
$trayName = 'MwbTray.ps1'
$localMain = Join-Path $LocalInstallDir $mainName
$localHelper = Join-Path $LocalInstallDir $helperName
$localTray = Join-Path $PSScriptRoot $trayName
$remoteMain = Join-Path $RemoteInstallDir $mainName
$remoteHelper = Join-Path $RemoteInstallDir $helperName
$remoteTray = Join-Path $RemoteInstallDir $trayName
if ([string]::IsNullOrWhiteSpace($RemoteUserProfile)) {
    $RemoteUserProfile = Join-Path 'C:\Users' (($RemoteInteractiveUser -split '\\')[-1])
}
$remoteSettings = Join-Path $RemoteUserProfile 'AppData\Local\Microsoft\PowerToys\MouseWithoutBorders\settings.json'

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
    $output = & ssh $RemoteHost "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" 2>&1
    if ($LASTEXITCODE -ne 0) {
        $diagnostic = (@($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        $diagnostic = $diagnostic -replace '[A-Za-z0-9+/=]{100,}', '<redacted-payload>'
        throw "Remote command failed on $RemoteHost (exit code $LASTEXITCODE): $diagnostic"
    }
    return @($output | ForEach-Object { [string]$_ })
}

function Write-RemoteFile {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [byte[]]$Bytes
    )

    $payload = [Convert]::ToBase64String($Bytes)
    $body = @(
        '$ErrorActionPreference = ''Stop'''
        '$ProgressPreference = ''SilentlyContinue'''
        ('$TargetPath = ''' + ($Path -replace "'", "''") + '''')
        '$raw = [Console]::In.ReadToEnd()'
        '[IO.File]::WriteAllBytes($TargetPath, [Convert]::FromBase64String($raw))'
    ) -join [Environment]::NewLine
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($body))
    $output = $payload | & ssh $RemoteHost "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand $encoded" 2>&1
    if ($LASTEXITCODE -ne 0) {
        $diagnostic = (@($output | ForEach-Object { [string]$_ }) -join [Environment]::NewLine)
        throw "Remote file write failed on $RemoteHost (exit code $LASTEXITCODE): $diagnostic"
    }
}

function Get-LocalSettingsPath {
    Join-Path $env:LOCALAPPDATA 'Microsoft\PowerToys\MouseWithoutBorders\settings.json'
}

function Get-SettingsObject([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    }
    catch {
        return $null
    }
}

function Write-SettingsObject([string]$Path, [object]$Settings) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $temporary = "$Path.mwb-enhanced.tmp"
    $backup = "$Path.mwb-enhanced.bak"
    if (Test-Path -LiteralPath $Path) {
        Copy-Item -LiteralPath $Path -Destination $backup -Force
    }
    $json = $Settings | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

function Get-RemoteSettingsJson {
    $output = Invoke-RemoteScript -Variables @{ SettingsPath = $remoteSettings } -Script @'
if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Remote MWB settings not found: $SettingsPath" }
$raw = [IO.File]::ReadAllText($SettingsPath)
try { $null = $raw | ConvertFrom-Json } catch { throw "Remote MWB settings are invalid: $($_.Exception.Message)" }
[Console]::Write([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($raw)))
'@
    $encoded = $output | Where-Object { $_ -match '^[A-Za-z0-9+/=]{100,}$' } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($encoded)) {
        throw "Could not read remote MWB settings from $RemoteHost."
    }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded))
}

function Get-MachineIdFromPool([string]$Pool, [string]$MachineName) {
    foreach ($entry in ($Pool -split ',')) {
        if ($entry -match '^([^:]+):(\d+)$' -and $Matches[1].Equals($MachineName, [StringComparison]::OrdinalIgnoreCase)) {
            return [int]$Matches[2]
        }
    }
    return $null
}

function Test-UsablePairingSettings([object]$Settings) {
    if ($null -eq $Settings -or $null -eq $Settings.properties) { return $false }
    $pool = [string]$Settings.properties.MachinePool.value
    $key = [string]$Settings.properties.SecurityKey.value
    $entries = @($pool -split ',' | Where-Object { $_ -match '^([^:]+):(\d+)$' })
    return (-not [string]::IsNullOrWhiteSpace($key) -and $entries.Count -ge 2)
}

function Sync-RemotePairingSettings([object]$LocalSettings) {
    $pairing = [pscustomobject]@{
        SecurityKey = $LocalSettings.properties.SecurityKey
        MachinePool = $LocalSettings.properties.MachinePool
        MachineMatrixString = $LocalSettings.properties.MachineMatrixString
        TCPPort = [int]$TcpPort
    }
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($pairing | ConvertTo-Json -Depth 10 -Compress)))
    Invoke-RemoteScript -Variables @{ SettingsPath = $remoteSettings; PairingPayload = $payload } -Script @'
if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Remote MWB settings not found: $SettingsPath" }
$pairing = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PairingPayload))) | ConvertFrom-Json
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
if (-not $settings.properties) { throw 'Remote MWB settings have no properties object.' }
foreach ($name in @('SecurityKey', 'MachinePool', 'MachineMatrixString')) { $settings.properties.$name = $pairing.$name }
$settings.properties.TCPPort.value = [int]$pairing.TCPPort
$temporary = "$SettingsPath.mwb-enhanced.tmp"
[IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $SettingsPath -Force
Write-Output 'REMOTE PAIRING SETTINGS READY'
'@
}

function Test-UsableMachineLayout([object]$Settings) {
    if ($null -eq $Settings -or $null -eq $Settings.properties) { return $false }
    $matrix = @($Settings.properties.MachineMatrixString)
    $pool = [string]$Settings.properties.MachinePool.value
    $available = @($pool -split ',' | Where-Object { $_ -match '^([^:]+):\d+$' } | ForEach-Object { $Matches[1] })
    $selected = @($matrix | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
    return ($matrix.Count -eq 4 -and $selected.Count -ge 2 -and (@($selected | Select-Object -Unique).Count -eq $selected.Count) -and (@($selected | Where-Object { $_ -in $available }).Count -eq $selected.Count))
}

function Sync-RemoteMachineLayout([object]$LocalSettings) {
    $layout = [pscustomobject]@{
        MachineMatrixString = @($LocalSettings.properties.MachineMatrixString)
        MatrixOneRow = [bool]$LocalSettings.properties.MatrixOneRow.value
    }
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($layout | ConvertTo-Json -Depth 10 -Compress)))
    Invoke-RemoteScript -Variables @{ SettingsPath = $remoteSettings; LayoutPayload = $payload } -Script @'
if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Remote MWB settings not found: $SettingsPath" }
$layout = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($LayoutPayload))) | ConvertFrom-Json
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
if (-not $settings.properties) { throw 'Remote MWB settings have no properties object.' }
$settings.properties.MachineMatrixString = @($layout.MachineMatrixString)
$settings.properties.MatrixOneRow.value = [bool]$layout.MatrixOneRow
$temporary = "$SettingsPath.mwb-enhanced.tmp"
[IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $SettingsPath -Force
Write-Output 'REMOTE MACHINE LAYOUT READY'
'@
}

function Sync-LocalPairingSettings([string]$RemoteSettingsJson) {
    $remote = $RemoteSettingsJson | ConvertFrom-Json
    if (-not $remote.properties.MachinePool.value -or @($remote.properties.MachinePool.value -split ',' | Where-Object { $_ -match ':' }).Count -lt 2) {
        throw 'Remote MWB MachinePool is not configured with two machines.'
    }

    $localPath = Get-LocalSettingsPath
    $local = Get-SettingsObject $localPath
    if ($null -eq $local) {
        $local = $remote
    }
    if ($null -eq $local.properties) {
        throw "Local MWB settings have no properties object: $localPath"
    }

    foreach ($name in @('SecurityKey', 'MachinePool', 'MachineMatrixString', 'TCPPort')) {
        $local.properties.$name = $remote.properties.$name
    }
    $local.properties.TCPPort.value = $TcpPort
    $localMachineId = Get-MachineIdFromPool ([string]$local.properties.MachinePool.value) $env:COMPUTERNAME
    if ($null -ne $localMachineId) {
        $local.properties.MachineID.value = $localMachineId
    }
    Write-SettingsObject $localPath $local
    Write-Output ("LOCAL SETTINGS READY TCP={0} MACHINE={1}" -f $TcpPort, $local.properties.MachineID.value)
}

function Sync-PairingSettings {
    $remoteJson = Get-RemoteSettingsJson
    $remote = $remoteJson | ConvertFrom-Json
    $local = Get-SettingsObject (Get-LocalSettingsPath)
    if (Test-UsablePairingSettings $remote) {
        $localLayout = $null
        if (Test-UsableMachineLayout $local) {
            $localLayout = $local
        }
        Sync-LocalPairingSettings $remoteJson
        if ($null -ne $localLayout) {
            Sync-RemoteMachineLayout $localLayout | Out-Null
            $updatedLocal = Get-SettingsObject (Get-LocalSettingsPath)
            $updatedLocal.properties.MachineMatrixString = @($localLayout.properties.MachineMatrixString)
            $updatedLocal.properties.MatrixOneRow.value = [bool]$localLayout.properties.MatrixOneRow.value
            Write-SettingsObject (Get-LocalSettingsPath) $updatedLocal
        }
    }
    elseif (Test-UsablePairingSettings $local) {
        Sync-RemotePairingSettings $local | Out-Null
        Sync-LocalPairingSettings (Get-RemoteSettingsJson)
    }
    else {
        throw 'Neither machine has a valid MWB pairing configuration. Configure the two machines once before using the enhanced launcher.'
    }
    Repair-RemoteSettings | Out-Null
}

function Repair-RemoteSettings {
    Invoke-RemoteScript -Variables @{ SettingsPath = $remoteSettings; TcpPort = $TcpPort } -Script @'
if (-not (Test-Path -LiteralPath $SettingsPath)) { throw "Remote MWB settings not found: $SettingsPath" }
$settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
if (-not $settings.properties) { throw 'Remote MWB settings have no properties object.' }
$settings.properties.TCPPort.value = [int]$TcpPort
$temporary = "$SettingsPath.mwb-enhanced.tmp"
[IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 30), [Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $SettingsPath -Force
Write-Output ("REMOTE SETTINGS READY TCP={0}" -f $TcpPort)
'@
}

function Get-LocalProcessPaths {
    @([IO.Path]::GetFullPath($localMain), [IO.Path]::GetFullPath($localHelper))
}

function Stop-LocalTray {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$localTray*" } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Stop-LocalMwb {
    Stop-LocalTray
    $paths = Get-LocalProcessPaths
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ExecutablePath -and
            (([IO.Path]::GetFullPath($_.ExecutablePath) -in $paths) -or $_.Name -in @('MouseWithoutBorders.exe', 'MouseWithoutBordersHelper.exe'))
        } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

    Get-NetTCPConnection -LocalPort $TcpPort, ($TcpPort + 1) -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        ForEach-Object {
            $process = Get-CimInstance Win32_Process -Filter "ProcessId = $_" -ErrorAction SilentlyContinue
            if ($process -and $process.Name -in @('MouseWithoutBorders.exe', 'PowerToys.MouseWithoutBorders.exe')) {
                Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
            }
        }
}

function Start-LocalTray {
    if (-not (Test-Path -LiteralPath $localTray)) { throw "Tray script not found: $localTray" }
    Stop-LocalTray
    $arguments = @(
        '-NoLogo', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-STA', '-WindowStyle', 'Hidden',
        '-File', "`"$localTray`"",
        '-MainPath', "`"$localMain`"",
        '-HelperPath', "`"$localHelper`"",
        '-SettingsPath', (Get-LocalSettingsPath),
        '-TcpPort', $TcpPort
    )
    Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -WorkingDirectory $LocalInstallDir -WindowStyle Hidden | Out-Null
}

function Start-LocalMwb {
    if (-not (Test-Path -LiteralPath $localMain)) { throw "Local MWB binary not found: $localMain" }
    Stop-LocalMwb
    $process = Start-Process -FilePath $localMain -WorkingDirectory $LocalInstallDir -PassThru
    Start-Sleep -Seconds 3
    $current = Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)"
    if (-not $current -or $current.SessionId -eq 0) {
        Stop-LocalMwb
        throw "Local MWB did not start in an interactive session. PID=$($process.Id)"
    }
    $helper = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($localHelper)) } | Select-Object -First 1
    if (-not $helper -and (Test-Path -LiteralPath $localHelper)) {
        Start-Process -FilePath $localHelper -WorkingDirectory $LocalInstallDir | Out-Null
    }
    Start-LocalTray
    Write-Output ("LOCAL STARTED PID={0} SESSION={1}" -f $process.Id, $current.SessionId)
}

function Wait-ForMwbConnection {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $main = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and ([IO.Path]::GetFullPath($_.ExecutablePath) -eq [IO.Path]::GetFullPath($localMain)) } |
            Select-Object -First 1
        if ($main) {
            $connections = @(Get-NetTCPConnection -OwningProcess $main.ProcessId -State Established -ErrorAction SilentlyContinue)
            if ($connections.Count -gt 0) {
                Write-Output ("MWB CONNECTED TCP={0}" -f $TcpPort)
                return
            }
        }
        Start-Sleep -Seconds 1
    }
    throw "MWB processes are running, but no established peer connection was detected on TCP $TcpPort/$($TcpPort + 1)."
}

function Install-RemoteTrayScript {
    if (-not (Test-Path -LiteralPath $localTray)) { throw "Tray script not found: $localTray" }
    Write-RemoteFile -Path $remoteTray -Bytes ([IO.File]::ReadAllBytes($localTray))
}

function Invoke-RemoteStop {
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteHelper = $remoteHelper; RemoteTray = $remoteTray; TcpPort = $TcpPort } -Script @'
$paths = @([IO.Path]::GetFullPath($RemoteMain), [IO.Path]::GetFullPath($RemoteHelper))
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object {
        ($_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -in $paths) -or
        ($_.CommandLine -and $_.CommandLine -like "*$RemoteTray*") -or
        $_.Name -in @('MouseWithoutBorders.exe', 'MouseWithoutBordersHelper.exe')
    } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
Get-NetTCPConnection -LocalPort ([uint16]$TcpPort), ([uint16]([int]$TcpPort + 1)) -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess -Unique |
    ForEach-Object {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId = $_" -ErrorAction SilentlyContinue
        if ($process -and $process.Name -in @('MouseWithoutBorders.exe', 'PowerToys.MouseWithoutBorders.exe')) {
            Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue
        }
    }
Write-Output 'REMOTE STOPPED'
'@
}

function Invoke-RemoteStart {
    Invoke-RemoteStop
    Install-RemoteTrayScript
    Invoke-RemoteScript -Variables @{
        RemoteMain = $remoteMain
        RemoteHelper = $remoteHelper
        RemoteTray = $remoteTray
        SettingsPath = $remoteSettings
        RemoteDir = $RemoteInstallDir
        InteractiveUser = $RemoteInteractiveUser
        TcpPort = $TcpPort
    } -Script @'
if (-not (Test-Path -LiteralPath $RemoteMain)) { throw "Remote MWB binary not found: $RemoteMain" }
$taskName = 'MwbEnhancedOneClick-' + [Guid]::NewGuid().ToString('N')
$action = New-ScheduledTaskAction -Execute $RemoteMain -WorkingDirectory $RemoteDir
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Force | Out-Null
try {
    Start-ScheduledTask -TaskName $taskName
    Start-Sleep -Seconds 4
    $current = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq [IO.Path]::GetFullPath($RemoteMain) } | Select-Object -First 1
    if (-not $current) { throw 'Remote MWB did not start.' }
    if ($current.SessionId -eq 0) { throw "Remote MWB started in Session 0 (PID=$($current.ProcessId))." }
    $helper = Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -eq [IO.Path]::GetFullPath($RemoteHelper) } | Select-Object -First 1
    if (-not $helper -and (Test-Path -LiteralPath $RemoteHelper)) {
        Start-Process -FilePath $RemoteHelper -WorkingDirectory $RemoteDir | Out-Null
    }

    $trayTask = $taskName + '-Tray'
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $trayArguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$RemoteTray`" -MainPath `"$RemoteMain`" -HelperPath `"$RemoteHelper`" -SettingsPath `"$SettingsPath`" -TcpPort $TcpPort"
    $trayAction = New-ScheduledTaskAction -Execute $powershell -Argument $trayArguments -WorkingDirectory $RemoteDir
    Register-ScheduledTask -TaskName $trayTask -Action $trayAction -Principal $principal -Force | Out-Null
    try { Start-ScheduledTask -TaskName $trayTask; Start-Sleep -Seconds 2 } finally { Unregister-ScheduledTask -TaskName $trayTask -Confirm:$false -ErrorAction SilentlyContinue }
    Write-Output ("REMOTE STARTED PID={0} SESSION={1} USER={2}" -f $current.ProcessId, $current.SessionId, $InteractiveUser)
}
finally {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
}
'@
}

function Show-Status {
    Write-Output '--- LOCAL / 本机 ---'
    $paths = Get-LocalProcessPaths
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { ($_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -in $paths) -or ($_.CommandLine -and $_.CommandLine -like "*$localTray*") } |
        Select-Object ProcessId, SessionId, Name, ExecutablePath, CommandLine | Format-Table -AutoSize
    Get-NetTCPConnection -LocalPort $TcpPort, ($TcpPort + 1) -ErrorAction SilentlyContinue |
        Select-Object LocalPort, State, OwningProcess | Format-Table -AutoSize
    Write-Output "--- $RemoteHost / 远端 ---"
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteHelper = $remoteHelper; RemoteTray = $remoteTray; TcpPort = $TcpPort } -Script @'
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { ($_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -in @([IO.Path]::GetFullPath($RemoteMain), [IO.Path]::GetFullPath($RemoteHelper))) -or ($_.CommandLine -and $_.CommandLine -like "*$RemoteTray*") } |
    Select-Object ProcessId, SessionId, Name, ExecutablePath, CommandLine | Format-Table -AutoSize
Get-NetTCPConnection -LocalPort ([uint16]$TcpPort), ([uint16]([int]$TcpPort + 1)) -ErrorAction SilentlyContinue |
    Select-Object LocalPort, State, OwningProcess | Format-Table -AutoSize
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
    Install-RemoteTrayScript
    Invoke-RemoteScript -Variables @{ RemoteMain = $remoteMain; RemoteHelper = $remoteHelper; RemoteTray = $remoteTray; SettingsPath = $remoteSettings; RemoteDir = $RemoteInstallDir; InteractiveUser = $RemoteInteractiveUser; Task = $TaskName; TcpPort = $TcpPort } -Script @'
if (-not (Test-Path -LiteralPath $RemoteMain)) { throw "Remote MWB binary not found: $RemoteMain" }
$principal = New-ScheduledTaskPrincipal -UserId $InteractiveUser -LogonType Interactive -RunLevel Highest
$action = New-ScheduledTaskAction -Execute $RemoteMain -WorkingDirectory $RemoteDir
Register-ScheduledTask -TaskName $Task -Action $action -Principal $principal -Force | Out-Null
$trayTask = $Task + '-Tray'
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$trayArguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$RemoteTray`" -MainPath `"$RemoteMain`" -HelperPath `"$RemoteHelper`" -SettingsPath `"$SettingsPath`" -TcpPort $TcpPort"
$trayAction = New-ScheduledTaskAction -Execute $powershell -Argument $trayArguments -WorkingDirectory $RemoteDir
Register-ScheduledTask -TaskName $trayTask -Action $trayAction -Principal $principal -Force | Out-Null
Write-Output "REMOTE AUTOSTART INSTALLED: $Task"
'@
}

function Remove-LocalAutostart {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName ($TaskName + '-Tray') -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "LOCAL AUTOSTART REMOVED: $TaskName"
}

function Remove-RemoteAutostart {
    Invoke-RemoteScript -Variables @{ Task = $TaskName } -Script @'
Unregister-ScheduledTask -TaskName $Task -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName ($Task + '-Tray') -Confirm:$false -ErrorAction SilentlyContinue
Write-Output "REMOTE AUTOSTART REMOVED: $Task"
'@
}

switch ($Action) {
    'start' {
        Sync-PairingSettings
        Start-LocalMwb
        Invoke-RemoteStart
        Wait-ForMwbConnection
    }
    'stop' {
        Stop-LocalMwb
        Invoke-RemoteStop
    }
    'restart' {
        Sync-PairingSettings
        Stop-LocalMwb
        Invoke-RemoteStop
        Start-Sleep -Seconds 2
        Start-LocalMwb
        Invoke-RemoteStart
        Wait-ForMwbConnection
    }
    'status' { Show-Status }
    'install-autostart' { Install-LocalAutostart; Install-RemoteAutostart }
    'remove-autostart' { Remove-LocalAutostart; Remove-RemoteAutostart }
}

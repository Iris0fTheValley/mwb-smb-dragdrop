[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$MainPath,

    [Parameter(Mandatory)]
    [string]$HelperPath,

    [int]$TcpPort = 15101
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$script:mainPath = [IO.Path]::GetFullPath($MainPath)
$script:helperPath = [IO.Path]::GetFullPath($HelperPath)
$script:busy = $false
$script:currentState = ''
$script:mutex = $null
$created = $false

try {
    $mutexName = 'Local\MwbEnhancedTray-' + ([IO.Path]::GetFileNameWithoutExtension($script:mainPath))
    $script:mutex = New-Object Threading.Mutex($false, $mutexName, [ref]$created)
    if (-not $created) {
        exit 0
    }

    $script:notifyIcon = New-Object Windows.Forms.NotifyIcon
    $script:notifyIcon.Visible = $true
    $script:notifyIcon.Icon = [Drawing.SystemIcons]::Application

    $script:statusItem = New-Object Windows.Forms.ToolStripMenuItem
    $script:statusItem.Enabled = $false

    $script:restartItem = New-Object Windows.Forms.ToolStripMenuItem('重启 MWB / Restart MWB')
    $script:stopItem = New-Object Windows.Forms.ToolStripMenuItem('关闭 MWB / Stop MWB')
    $script:exitItem = New-Object Windows.Forms.ToolStripMenuItem('退出托盘 / Exit tray')

    $menu = New-Object Windows.Forms.ContextMenuStrip
    [void]$menu.Items.Add($script:statusItem)
    [void]$menu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($script:restartItem)
    [void]$menu.Items.Add($script:stopItem)
    [void]$menu.Items.Add((New-Object Windows.Forms.ToolStripSeparator))
    [void]$menu.Items.Add($script:exitItem)
    $script:notifyIcon.ContextMenuStrip = $menu

    function Get-MwbProcesses {
        $paths = @($script:mainPath, $script:helperPath)
        @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ExecutablePath -and $paths -contains ([IO.Path]::GetFullPath($_.ExecutablePath)) })
    }

    function Get-MwbState {
        $processes = Get-MwbProcesses
        $main = $processes | Where-Object { [IO.Path]::GetFullPath($_.ExecutablePath) -eq $script:mainPath } | Select-Object -First 1
        $helper = $processes | Where-Object { [IO.Path]::GetFullPath($_.ExecutablePath) -eq $script:helperPath } | Select-Object -First 1

        if (-not $main) { return '已停止 / Stopped' }
        if (-not $helper) { return '运行中，Helper 缺失 / Running, helper missing' }

        $connected = $false
        try {
            $connected = @(Get-NetTCPConnection -OwningProcess $main.ProcessId -State Established -ErrorAction SilentlyContinue).Count -gt 0
        }
        catch {
            $connected = $false
        }

        if ($connected) { return '已连接 / Connected' }
        return "运行中，未连接 / Running, disconnected (TCP $TcpPort/$($TcpPort + 1))"
    }

    function Update-MwbState {
        $state = Get-MwbState
        if ($state -eq $script:currentState) { return }
        $script:currentState = $state
        $script:statusItem.Text = "状态 / Status: $state"
        $script:notifyIcon.Text = "MWB: $state"
        if ($state -like '*Connected*') {
            $script:notifyIcon.Icon = [Drawing.SystemIcons]::Information
        }
        elseif ($state -like '*Stopped*') {
            $script:notifyIcon.Icon = [Drawing.SystemIcons]::Error
        }
        else {
            $script:notifyIcon.Icon = [Drawing.SystemIcons]::Warning
        }
    }

    function Stop-Mwb {
        @(Get-MwbProcesses) | ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    }

    function Start-Mwb {
        if (-not (Test-Path -LiteralPath $script:mainPath)) {
            throw "MWB binary not found: $script:mainPath"
        }

        Stop-Mwb
        $process = Start-Process -FilePath $script:mainPath -WorkingDirectory ([IO.Path]::GetDirectoryName($script:mainPath)) -PassThru
        Start-Sleep -Seconds 2
        if (-not (Get-MwbProcesses | Where-Object { $_.ProcessId -eq $process.Id })) {
            throw "MWB failed to start: PID=$($process.Id)"
        }

        Start-Sleep -Seconds 2
        if ((Test-Path -LiteralPath $script:helperPath) -and -not (Get-MwbProcesses | Where-Object { [IO.Path]::GetFullPath($_.ExecutablePath) -eq $script:helperPath })) {
            Start-Process -FilePath $script:helperPath -WorkingDirectory ([IO.Path]::GetDirectoryName($script:helperPath)) | Out-Null
        }
    }

    function Invoke-MwbAction([scriptblock]$action) {
        if ($script:busy) { return }
        $script:busy = $true
        $script:restartItem.Enabled = $false
        $script:stopItem.Enabled = $false
        try {
            & $action
        }
        catch {
            [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'MWB', [Windows.Forms.MessageBoxButtons]::OK, [Windows.Forms.MessageBoxIcon]::Error) | Out-Null
        }
        finally {
            $script:busy = $false
            $script:restartItem.Enabled = $true
            $script:stopItem.Enabled = $true
            Update-MwbState
        }
    }

    $script:restartItem.add_Click({ Invoke-MwbAction { Start-Mwb } })
    $script:stopItem.add_Click({ Invoke-MwbAction { Stop-Mwb } })
    $script:notifyIcon.add_DoubleClick({ Invoke-MwbAction { Start-Mwb } })
    $script:exitItem.add_Click({
        $script:notifyIcon.Visible = $false
        $script:notifyIcon.Dispose()
        [Windows.Forms.Application]::ExitThread()
    })

    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 3000
    $timer.add_Tick({ Update-MwbState })
    $timer.Start()
    Update-MwbState
    [Windows.Forms.Application]::Run()
}
finally {
    if ($script:notifyIcon) {
        $script:notifyIcon.Visible = $false
        $script:notifyIcon.Dispose()
    }
    if ($script:mutex) {
        try { $script:mutex.ReleaseMutex() } catch { }
        $script:mutex.Dispose()
    }
}

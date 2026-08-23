[CmdletBinding()]
param(
    [ValidateSet('status', 'start', 'stop', 'restart')]
    [string]$Action = 'status',

    [string]$RemoteHost = 'pc-b',
    [string]$LocalInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInstallDir = 'C:\AgentWork\mwb-enhanced',
    [string]$RemoteInteractiveUser = 'ID-BLUEBERRY\12298',
    [string]$RemoteUserProfile = '',
    [int]$TcpPort = 15101
)

$arguments = @{
    Action = $Action
    RemoteHost = $RemoteHost
    LocalInstallDir = $LocalInstallDir
    RemoteInstallDir = $RemoteInstallDir
    RemoteInteractiveUser = $RemoteInteractiveUser
    TcpPort = $TcpPort
}
if (-not [string]::IsNullOrWhiteSpace($RemoteUserProfile)) {
    $arguments.RemoteUserProfile = $RemoteUserProfile
}

& (Join-Path $PSScriptRoot 'Start-MwbEnhanced.ps1') @arguments
exit $LASTEXITCODE

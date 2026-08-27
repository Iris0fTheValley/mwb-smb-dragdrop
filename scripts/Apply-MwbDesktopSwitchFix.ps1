[CmdletBinding()]
param(
    [string]$SourceRoot = 'C:\AgentWork\PowerToys-main\PowerToys-main'
)

$ErrorActionPreference = 'Stop'
$path = Join-Path $SourceRoot 'src\modules\MouseWithoutBorders\App\Core\WinAPI.cs'
if (-not (Test-Path -LiteralPath $path)) {
    throw "MWB source file not found: $path"
}

$old = @'
            if (!IsMyDesktopActive() || Common.CurrentProcess.SessionId != NativeMethods.WTSGetActiveConsoleSessionId())
            {
                Helper.RunDDHelper(true);
                int waitCount = 20;

                while (NativeMethods.WTSGetActiveConsoleSessionId() == 0xFFFFFFFF && waitCount > 0)
                {
                    waitCount--;
                    Logger.LogDebug("The session is detached/attached.");
                    Thread.Sleep(500);
                }

                string myDesktop = GetMyDesktop();
                activeDesktop = GetInputDesktop();

                Logger.LogDebug("*** Active Desktop = " + activeDesktop);
                Logger.LogDebug("*** My Desktop = " + myDesktop);
'@

$new = @'
            if (!IsMyDesktopActive() || Common.CurrentProcess.SessionId != NativeMethods.WTSGetActiveConsoleSessionId())
            {
                string myDesktop = GetMyDesktop();
                activeDesktop = GetInputDesktop();

                // OpenInputDesktop/GetUserObjectInformation can briefly fail while an injected
                // cross-machine click changes the input desktop. An empty name is not evidence
                // of a real desktop switch; do not kill the helper or close MWB sockets here.
                if (string.IsNullOrWhiteSpace(myDesktop) || string.IsNullOrWhiteSpace(activeDesktop))
                {
                    Logger.LogDebug("*** Desktop query unavailable; deferring desktop-switch handling.");
                    return;
                }

                if (myDesktop.Equals(activeDesktop, StringComparison.OrdinalIgnoreCase) &&
                    Common.CurrentProcess.SessionId == NativeMethods.WTSGetActiveConsoleSessionId())
                {
                    return;
                }

                Helper.RunDDHelper(true);
                int waitCount = 20;

                while (NativeMethods.WTSGetActiveConsoleSessionId() == 0xFFFFFFFF && waitCount > 0)
                {
                    waitCount--;
                    Logger.LogDebug("The session is detached/attached.");
                    Thread.Sleep(500);
                }

                myDesktop = GetMyDesktop();
                activeDesktop = GetInputDesktop();

                Logger.LogDebug("*** Active Desktop = " + activeDesktop);
                Logger.LogDebug("*** My Desktop = " + myDesktop);

                if (string.IsNullOrWhiteSpace(myDesktop) || string.IsNullOrWhiteSpace(activeDesktop))
                {
                    Logger.LogDebug("*** Desktop query unavailable after session wait; keeping MWB alive.");
                    return;
                }
'@

$content = (Get-Content -LiteralPath $path -Raw).Replace("`r`n", "`n")
$matches = ([regex]::Matches($content, [regex]::Escape($old))).Count
if ($matches -eq 0) {
    if ($content.Contains('Desktop query unavailable; deferring desktop-switch handling.')) {
        Write-Output "MWB desktop-switch fix already applied: $path"
        exit 0
    }
    throw "Expected desktop-switch block was not found in $path"
}
if ($matches -ne 1) {
    throw "Expected one desktop-switch block in $path, found $matches"
}

$backup = "$path.mwb-desktop-switch.bak"
Copy-Item -LiteralPath $path -Destination $backup -Force
$updated = $content.Replace($old, $new)
[IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
Write-Output "MWB desktop-switch fix applied: $path"
Write-Output "Backup: $backup"

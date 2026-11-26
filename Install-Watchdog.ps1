# Register MemWatchdog.ps1 as a scheduled task (every 10 min + at logon, as you, with admin rights for service restarts),
# plus 'WatchCatFix': an on-demand task (no trigger) that runs Run-Queued.ps1 with admin rights. The WatchCat window
# starts it for service restarts / targeted fixes, so those no longer ask for admin confirmation every time.
# Run with -Uninstall to remove both. -Quiet: don't wait for Enter at the end.
param([switch]$Uninstall, [switch]$Quiet)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $extra = (@(if ($Uninstall) { '-Uninstall' }) + @(if ($Quiet) { '-Quiet' })) -join ' '
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" $extra"
    exit
}

$taskName = 'MemWatchdog'
if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'WatchCatFix' -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'Watchdog tasks removed.'
    if (-not $Quiet) { Read-Host 'Press Enter to close' }; exit
}

$script = Join-Path $PSScriptRoot 'MemWatchdog.ps1'
$user = "$env:USERDOMAIN\$env:USERNAME"
# conhost --headless keeps the window hidden
$action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`""
$trigger = @(
    (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 10)),
    (New-ScheduledTaskTrigger -AtLogOn -User $user)
)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
# On-demand fix task: no trigger, started by the WatchCat window; queued requests run one after another
$fixAction = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'Run-Queued.ps1')`""
$fixSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances Queue -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
Register-ScheduledTask -TaskName 'WatchCatFix' -Action $fixAction -Principal $principal -Settings $fixSettings -Force | Out-Null
Write-Host "Installed: '$taskName' runs every 10 minutes and at logon. Log: $(Join-Path $PSScriptRoot 'MemWatchdog.log')"
Write-Host "Installed: 'WatchCatFix' runs fixes for the WatchCat window without asking for admin each time. Log: $(Join-Path $PSScriptRoot 'data\queue.log')"
if (-not $Quiet) { Read-Host 'Press Enter to close' }

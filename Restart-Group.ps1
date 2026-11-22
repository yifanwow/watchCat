# Restart a group of processes / services in order ("restartGroup" rules, and the rule wizard's "try once").
# Each step is { "service": "Name" } or { "process": "name", "args": "start arguments", "path": "full path to the exe" }.
# A process is stopped and, if nothing brings it back within 3 s, started again the way it was started before
# (same path and arguments). Services go through Restart-ServiceHard.ps1, which needs admin.
# Usage: Restart-Group.ps1 -StepsB64 <base64 of the UTF-8 JSON array>   (from the WatchDog window)
#        Restart-Group.ps1 -Steps $rule.fix.steps                       (from MemWatchdog.ps1)
param([object]$Steps, [string]$StepsB64, [switch]$Quiet)

if ($StepsB64) { $Steps = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($StepsB64))) }
foreach ($s in @($Steps)) {
    if (-not $s) { continue }
    if ($s.service) {
        & (Join-Path $PSScriptRoot 'Restart-ServiceHard.ps1') -Name $s.service | Out-Null
    } elseif ($s.process) {
        $procs = @(Get-Process -Name $s.process -ErrorAction SilentlyContinue)
        $path = ($procs | Where-Object Path | Select-Object -First 1).Path
        if (-not $path) { $path = $s.path }
        $procs | Stop-Process -Force -ErrorAction SilentlyContinue
        if ($procs) { Start-Sleep -Seconds 3 }
        if ($path -and (Test-Path $path) -and -not (Get-Process -Name $s.process -ErrorAction SilentlyContinue)) {
            if ($s.args) { Start-Process -FilePath $path -ArgumentList $s.args -WorkingDirectory (Split-Path $path) }
            else { Start-Process -FilePath $path -WorkingDirectory (Split-Path $path) }
        }
    }
    Start-Sleep -Seconds 2
}
if (-not $Quiet) { Write-Host 'done' }

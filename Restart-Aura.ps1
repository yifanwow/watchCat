# 重启华硕 Aura 灯效服务（LightingService），并让灯效恢复成重启前的样子。
# 灯效服务重启后只记得"用的是自定义脚本（模式 100）"，脚本本身要由奥创中心服务（ArmouryCrateService）重新下发——
# 它只在自己启动时或屏幕点亮时才发。所以这里重启灯效服务后，接着重启奥创中心服务，再等灯效服务的日志里出现
# "收到灯效脚本"（put_SetScript）的新记录，才算恢复。需要管理员权限；不是管理员时自己请求提升。
param([switch]$Quiet)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Quiet"
    exit
}
$hard = Join-Path $PSScriptRoot 'Restart-ServiceHard.ps1'
$log = 'C:\ProgramData\ASUS\ARMOURY CRATE Diagnosis\LightingService\LightingService.log'
function Wait-Running($name, $sec) {
    for ($i = 0; $i -lt $sec; $i++) { if ((Get-Service $name -ErrorAction SilentlyContinue).Status -eq 'Running') { return $true }; Start-Sleep -Seconds 1 }
    Start-Service $name -ErrorAction SilentlyContinue   # 有时重启后会自己停掉：再启动一次
    for ($i = 0; $i -lt 20; $i++) { if ((Get-Service $name -ErrorAction SilentlyContinue).Status -eq 'Running') { return $true }; Start-Sleep -Seconds 1 }
    $false
}
$mark = if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
& $hard -Name LightingService | Out-Null
$ls = Wait-Running 'LightingService' 30
if ((Get-Service ArmouryCrateService -ErrorAction SilentlyContinue)) {
    Start-Sleep -Seconds 3
    & $hard -Name ArmouryCrateService | Out-Null
    $ac = Wait-Running 'ArmouryCrateService' 30
} else { $ac = $false }
# 等奥创中心把灯效脚本重新发给灯效服务（只看重启之后新写的日志）
$applied = $false
for ($i = 0; $i -lt 90 -and -not $applied; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $log) {
        $f = Get-Item $log
        $start = if ($f.Length -ge $mark) { $mark } else { 0 }   # 日志换了新文件就从头看
        $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite')
        try { [void]$fs.Seek($start, 'Begin'); $sr = New-Object IO.StreamReader($fs); $new = $sr.ReadToEnd() } finally { $fs.Dispose() }
        if ($new -match 'put_SetScript\] Out') { $applied = $true }
    }
}
Write-Host ("Aura restarted: LightingService running {0}, Armoury Crate service running {1}, lighting effect re-applied {2}" -f $ls, $ac, $applied)
if (-not ($ls -and $applied)) { exit 1 }

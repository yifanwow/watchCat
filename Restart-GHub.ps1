# 重启罗技 G HUB 的后台（lghub_agent）和托盘，并保证和开机时一样：
# 托盘按开机自启的方式带 --minimized 启动（不弹窗口），由托盘把 agent 带起来；鼠标键盘的设置和灯效由 agent 重新下发。
# 需要管理员权限（G HUB 以管理员身份运行）；不是管理员时自己请求提升。看门狗和监控窗口的规则 "lghub-agent" 会调用它。
param([switch]$Quiet)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Quiet"
    exit
}
$tray = Get-Process lghub_system_tray -ErrorAction SilentlyContinue | Select-Object -First 1
$path = if ($tray -and $tray.Path) { $tray.Path } else { Join-Path $env:ProgramFiles 'LGHUB\system_tray\lghub_system_tray.exe' }
if (-not (Test-Path $path)) { Write-Host "G HUB not found: $path"; exit 1 }
Get-Process lghub_agent, lghub_system_tray -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
for ($i = 0; $i -lt 10 -and (Get-Process lghub_agent, lghub_system_tray -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
Start-Process -FilePath $path -ArgumentList '--minimized' -WorkingDirectory (Split-Path $path)
# 托盘一般几秒内把 agent 拉起来；没有的话自己启动 agent
for ($i = 0; $i -lt 30 -and -not (Get-Process lghub_agent -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Seconds 1 }
if (-not (Get-Process lghub_agent -ErrorAction SilentlyContinue)) {
    $agent = Join-Path (Split-Path (Split-Path $path)) 'lghub_agent.exe'
    if (Test-Path $agent) { Start-Process -FilePath $agent -WorkingDirectory (Split-Path $agent) }
}
Write-Host ("G HUB restarted: tray {0}, agent {1}" -f [bool](Get-Process lghub_system_tray -ErrorAction SilentlyContinue), [bool](Get-Process lghub_agent -ErrorAction SilentlyContinue))

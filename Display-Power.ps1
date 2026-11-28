# 屏幕关闭时暂停 NVIDIA App 后台（NvContainerLocalSystem），屏幕点亮后恢复。
# 原因：屏幕关着、电脑开着时，NVIDIA App 的显示组件（nvxdapix.dll）不停开线程查显示配置，
# 每次在显卡内核 / 图形子系统里留下约 40 KB（分页池 Gpbm / VdPN），关屏一晚能涨 2–3 GB，重启服务也收不回，只能重启电脑。
# 由计划任务 'WatchCatDisplay' 在 Kernel-Power 566（显示会话切换）时运行，需要管理员。
# 只看最新一条 566：NextSessionType=1 是屏幕关闭，=0 是屏幕点亮。只恢复自己暂停的那次（data\nvapp-paused.flag）。
# 设置里关掉（settings.json 的 nvPauseScreenOff=false）后：不再暂停，已经暂停的照样恢复。
# 日志：data\display-power.log
param([string]$Test)   # -Test off / on：不看事件，直接按关屏 / 亮屏处理（测试用）

$svc = 'NvContainerLocalSystem'
$data = Join-Path $PSScriptRoot 'data'
$flag = Join-Path $data 'nvapp-paused.flag'
$log = Join-Path $data 'display-power.log'
function Log($m) {
    if ((Test-Path $log) -and (Get-Item $log).Length -gt 100KB) { Remove-Item $log -Force }
    Add-Content -Path $log -Value ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $m) -Encoding UTF8
}

$s = Get-Service $svc -ErrorAction SilentlyContinue
if (-not $s) { exit }   # 没装 NVIDIA App

if ($Test) { $next = if ($Test -eq 'off') { 1 } else { 0 } }
else {
    $e = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 566 } -MaxEvents 1 -ErrorAction SilentlyContinue
    if (-not $e) { exit }
    $d = @{}; foreach ($n in ([xml]$e.ToXml()).Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
    $next = [int]$d['NextSessionType']
}

$on = $true
try {
    $st = Get-Content (Join-Path $data 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($st.PSObject.Properties['nvPauseScreenOff'] -and -not $st.nvPauseScreenOff) { $on = $false }
} catch { }

if ($next -eq 1) {
    if (-not $on) { exit }
    if ($s.Status -ne 'Running') { exit }   # 本来就没在运行：不动，也不记标记（亮屏时就不会去启动它）
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
    if ((Get-Service $svc).Status -eq 'Stopped') { Set-Content $flag (Get-Date -Format s); Log '屏幕关闭：已暂停 NVIDIA App 后台' }
    else { Log '屏幕关闭：NVIDIA App 后台没能停下' }
} elseif ($next -eq 0) {
    if (-not (Test-Path $flag)) { exit }
    Remove-Item $flag -Force
    if ((Get-Service $svc).Status -ne 'Running') { Start-Service $svc -ErrorAction SilentlyContinue }
    Log ('屏幕点亮：' + $(if ((Get-Service $svc).Status -eq 'Running') { '已恢复 NVIDIA App 后台' } else { 'NVIDIA App 后台没能启动' }))
}

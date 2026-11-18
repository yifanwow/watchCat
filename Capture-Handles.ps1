# 记录句柄现场：某个程序的句柄在暴涨时，把"它手里攥着的是什么句柄、当时电脑上发生了什么"写成一份文本，
# 放在 data\incidents\（只在本机，不进 git）。事后对着这份记录就能判断是哪个注册表项 / 哪个模块的线程在漏。
# 看门狗（管理员权限）发现句柄连续暴涨时自动调用；也可以手动运行，或在监控窗口里右键程序 → 立即记录句柄现场。
#   Capture-Handles.ps1 -Name ROGLiveService        （同名的取句柄最多的那个进程）
#   Capture-Handles.ps1 -Id 8472 -Reason "句柄 10 分钟 +2,816"
# 需要管理员权限才能看系统服务的句柄名字。只读：只复制句柄来查询，查完立即关闭，不改动任何进程。
param([string]$Name, [int]$Id = 0, [string]$Reason = '手动记录', [switch]$Open)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'data\incidents'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
if (-not ('HandleProbe' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'HandleProbe.cs') -ReferencedAssemblies System.Core }

$p = if ($Id -gt 0) { Get-Process -Id $Id } else { Get-Process -Name $Name | Sort-Object HandleCount -Descending | Select-Object -First 1 }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$os = Get-CimInstance Win32_OperatingSystem
$up = (Get-Date) - $os.LastBootUpTime
$svc = @(Get-CimInstance Win32_Service -Filter "ProcessId=$($p.Id)" | ForEach-Object { $_.Name }) -join ', '
$path = try { $p.MainModule.FileName } catch { (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").ExecutablePath }
$ver = if ($path -and (Test-Path $path)) { (Get-Item $path).VersionInfo.ProductVersion } else { '' }
$started = try { $p.StartTime.ToString('yyyy-MM-dd HH:mm') } catch { '?' }

$o = New-Object Text.StringBuilder
function L($s) { [void]$o.AppendLine($s) }
L '=== WatchDog 句柄现场记录 ==='
L ('记录时间  {0:yyyy-MM-dd HH:mm:ss}    开机已 {1:N1} 天（{2:yyyy-MM-dd HH:mm} 开机）    管理员权限：{3}' -f (Get-Date), $up.TotalDays, $os.LastBootUpTime, $(if ($isAdmin) { '有' } else { '没有（系统服务的句柄名字读不到）' }))
L ('为什么记录  {0}' -f $Reason)
L ('程序  {0}  #{1}    启动于 {2}    句柄 {3:N0}    内存 {4:N0} MB    线程 {5}' -f $p.ProcessName, $p.Id, $started, $p.HandleCount, ($p.PrivateMemorySize64 / 1MB), $p.Threads.Count)
L ('路径  {0}    版本 {1}' -f $path, $ver)
if ($svc) { L ('服务  {0}' -f $svc) }
L ''
L ([HandleProbe]::Probe($p.Id, 20000))

L '--- 最近 12 小时的电源 / 屏幕事件 ---'
L '  （Kernel-Power 566 是"会话切换"：NextSessionType=1 一般是屏幕熄灭 / 进入待机，=0 是屏幕点亮；42 睡眠，107 从睡眠恢复，506 / 507 进入 / 退出现代待机）'
try {
    $ev = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; StartTime = (Get-Date).AddHours(-12) } -ErrorAction Stop |
        Where-Object { $_.Id -in 566, 42, 107, 506, 507 } | Sort-Object TimeCreated
    foreach ($e in $ev) {
        $x = [xml]$e.ToXml(); $d = @{}; foreach ($n in $x.Event.EventData.Data) { $d[$n.Name] = $n.'#text' }
        $extra = if ($e.Id -eq 566) { 'NextSessionType={0} Reason={1}' -f $d['NextSessionType'], $d['Reason'] } else { ($e.Message -split "`n")[0] }
        L ('  {0:MM-dd HH:mm:ss}  Kernel-Power {1}  {2}' -f $e.TimeCreated, $e.Id, $extra)
    }
    if (-not $ev) { L '  （没有）' }
} catch { L '  （没有）' }

L '--- 最近 2 小时：服务意外终止 / 新装服务 / 程序崩溃 ---'
try {
    $ev = @(Get-WinEvent -FilterHashtable @{ LogName = 'System'; Id = 7031, 7034, 7045; StartTime = (Get-Date).AddHours(-2) } -ErrorAction SilentlyContinue) +
          @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1002; StartTime = (Get-Date).AddHours(-2) } -ErrorAction SilentlyContinue)
    foreach ($e in ($ev | Sort-Object TimeCreated)) { $m = ($e.Message -split "`n")[0]; L ('  {0:MM-dd HH:mm:ss}  {1} {2}  {3}' -f $e.TimeCreated, $e.ProviderName, $e.Id, $m.Substring(0, [Math]::Min(150, $m.Length))) }
    if (-not $ev) { L '  （没有）' }
} catch { L '  （没有）' }

L '--- 最近 1 小时启动的程序 ---'
$recent = Get-Process | Where-Object { try { $_.StartTime -gt (Get-Date).AddHours(-1) } catch { $false } } | Sort-Object StartTime
foreach ($r in $recent) { L ('  {0:HH:mm:ss}  {1} #{2}' -f $r.StartTime, $r.ProcessName, $r.Id) }
if (-not $recent) { L '  （没有，或者读不到启动时间）' }

L '--- 句柄最多的 8 个进程（全系统）---'
foreach ($r in (Get-Process | Sort-Object HandleCount -Descending | Select-Object -First 8)) { L ('  {0,8:N0}  {1} #{2}' -f $r.HandleCount, $r.ProcessName, $r.Id) }
$k = Get-CimInstance Win32_PerfRawData_PerfOS_Memory
L '--- 内核内存池 ---'
L ('  分页池 {0:N0} MB    非分页池 {1:N0} MB    全系统句柄 {2:N0}' -f ($k.PoolPagedBytes / 1MB), ($k.PoolNonpagedBytes / 1MB), ((Get-Process | Measure-Object HandleCount -Sum).Sum))
L ([HandleProbe]::PoolTop(6))

$safe = ($p.ProcessName -replace '[^\w\.\-]', '_')
$file = Join-Path $dir ('{0:yyyyMMdd-HHmmss}-{1}.txt' -f (Get-Date), $safe)
[IO.File]::WriteAllText($file, $o.ToString(), (New-Object Text.UTF8Encoding($true)))
# 只留最近的 40 份
Get-ChildItem $dir -Filter '*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 40 | Remove-Item -Force -ErrorAction SilentlyContinue
$line = '{0:yyyy-MM-dd HH:mm:ss} CAPTURE {1} #{2} handles {3}: {4} -> data\incidents\{5}' -f (Get-Date), $p.ProcessName, $p.Id, $p.HandleCount, $Reason, (Split-Path $file -Leaf)
Add-Content -Path (Join-Path $PSScriptRoot 'MemWatchdog.log') -Value $line -Encoding UTF8
Write-Host $line
if ($Open) { Start-Process notepad.exe -ArgumentList "`"$file`"" }

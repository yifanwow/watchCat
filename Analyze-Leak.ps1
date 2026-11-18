# 分析内存泄漏：看一个程序"在忙什么、漏的是什么"，写成一份文本放在 data\incidents\（只在本机，不进 git）。
#   - 哪个线程一直在耗 CPU，抓几十次调用栈，看时间花在哪个 DLL 的哪个函数（后台不停轮询的循环会在这里现形）
#   - 把它的私有内存读一遍，统计重复最多的字符串（同一份数据存了成千上万遍 = 每次生成新的、旧的没释放）
#   - 从看门狗的记录算它最近 24 小时的上涨速度
# 在监控窗口里右键程序 → 分析内存泄漏；或手动：Analyze-Leak.ps1 -Name CorsairCpuIdService -Open
# 需要管理员权限才能看系统服务。只读：只读取内存和寄存器，抓调用栈时让目标线程暂停约 1 毫秒，不改动任何东西。约 1 分钟。
param([string]$Name, [int]$Id = 0, [switch]$Open)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $PSScriptRoot 'data\incidents'
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
if (-not ('LeakProbe' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'LeakProbe.cs') -ReferencedAssemblies System.Core }

$p = if ($Id -gt 0) { Get-Process -Id $Id } else { Get-Process -Name $Name | Sort-Object PrivateMemorySize64 -Descending | Select-Object -First 1 }
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$svc = @(Get-CimInstance Win32_Service -Filter "ProcessId=$($p.Id)" | ForEach-Object { $_.Name }) -join ', '
$path = (Get-CimInstance Win32_Process -Filter "ProcessId=$($p.Id)").ExecutablePath
$ver = if ($path -and (Test-Path $path)) { (Get-Item $path).VersionInfo.ProductVersion } else { '' }

$o = New-Object Text.StringBuilder
function L($s) { [void]$o.AppendLine($s) }
L '=== WatchDog 内存泄漏分析 ==='
L ('分析时间  {0:yyyy-MM-dd HH:mm:ss}    管理员权限：{1}' -f (Get-Date), $(if ($isAdmin) { '有' } else { '没有（系统服务看不了）' }))
L ('路径  {0}    版本 {1}' -f $path, $ver)
if ($svc) { L ('服务  {0}' -f $svc) }

# 看门狗记录里的走势：最近 24 小时（同名进程合计）
try {
    $files = Get-ChildItem (Join-Path $PSScriptRoot 'data') -Filter 'proc*.csv' | Sort-Object LastWriteTime | Select-Object -Last 2
    $since = (Get-Date).AddHours(-24)
    $pts = foreach ($f in $files) { Import-Csv $f.FullName | Where-Object { $_.Name -eq $p.ProcessName } | ForEach-Object { $t = [datetime]::ParseExact($_.Time, 'yyyy-MM-dd HH:mm:ss', $null); if ($t -ge $since) { [pscustomobject]@{ T = $t; MB = [double]$_.PrivMB; Cpu = [double]$_.CpuSec } } } }
    $pts = @($pts | Sort-Object T)
    if ($pts.Count -ge 2) {
        $h = ($pts[-1].T - $pts[0].T).TotalHours
        L ('看门狗记录（同名的所有进程合计）  最近 {0:N1} 小时：{1:N0} → {2:N0} MB（约 {3:+0.0;-0.0} MB/小时），这段时间平均占一个核的 {4:N1}% CPU' -f $h, $pts[0].MB, $pts[-1].MB, (($pts[-1].MB - $pts[0].MB) / $h), (100 * ($pts[-1].Cpu - $pts[0].Cpu) / $h / 3600))
    }
} catch {}
L ''
L ([LeakProbe]::Analyze($p.Id, 40))

$safe = ($p.ProcessName -replace '[^\w\.\-]', '_')
$file = Join-Path $dir ('{0:yyyyMMdd-HHmmss}-leak-{1}.txt' -f (Get-Date), $safe)
[IO.File]::WriteAllText($file, $o.ToString(), (New-Object Text.UTF8Encoding($true)))
Get-ChildItem $dir -Filter '*.txt' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 40 | Remove-Item -Force -ErrorAction SilentlyContinue
$line = '{0:yyyy-MM-dd HH:mm:ss} ANALYZE {1} #{2} {3:N0} MB -> data\incidents\{4}' -f (Get-Date), $p.ProcessName, $p.Id, ($p.PrivateMemorySize64 / 1MB), (Split-Path $file -Leaf)
Add-Content -Path (Join-Path $PSScriptRoot 'MemWatchdog.log') -Value $line -Encoding UTF8
Write-Host $line
if ($Open) { Start-Process notepad.exe -ArgumentList "`"$file`"" }

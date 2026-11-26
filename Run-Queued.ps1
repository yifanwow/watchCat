# 后台代办：WatchCat 窗口本身是普通权限。要重启服务、跑专项修复这类需要管理员权限的事时，
# 它把要做的事写成 data\queue\*.json，再启动计划任务「WatchCatFix」（安装时确认过一次管理员权限，之后启动它不再弹确认窗口），
# 由这个脚本以管理员权限依次执行。
#
# 安全：只执行下面白名单里、和本脚本在同一个文件夹的脚本；参数按「参数名 → 值」传进去（不拼成命令行、不当代码执行）。
# 每条请求执行完就删掉；结果写进 data\queue.log（超过 200 KB 清空）。
$ErrorActionPreference = 'Continue'
$allowed = @{
    'Restart-ServiceHard.ps1' = @('Name')
    'Restart-Group.ps1'       = @('StepsB64', 'Quiet')
    'Restart-Aura.ps1'        = @('Quiet')
    'Restart-GHub.ps1'        = @('Quiet')
    'Restart-SteamWeb.ps1'    = @('Quiet')
    'Fix-NvTopps.ps1'         = @('Quiet')
    'Analyze-Leak.ps1'        = @('Name', 'Id', 'Open')
    'Capture-Handles.ps1'     = @('Name', 'Id', 'Reason', 'Open')
}
$queue = Join-Path $PSScriptRoot 'data\queue'
$log = Join-Path $PSScriptRoot 'data\queue.log'
if (-not (Test-Path $queue)) { exit 0 }
if ((Test-Path $log) -and (Get-Item $log).Length -gt 200KB) { Remove-Item $log -Force }
function Write-QLog($m) { Add-Content -Path $log -Value ("{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $m) -Encoding UTF8 }

# 一次把排队的全做完（做的过程中又来了新的，也接着做）；太旧的（10 分钟前）丢掉，免得很久以后突然执行
for ($round = 0; $round -lt 20; $round++) {
    $files = @(Get-ChildItem $queue -Filter '*.json' -File -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($files.Count -eq 0) { break }
    foreach ($f in $files) {
        $req = $null
        try { $req = Get-Content $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { }
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
        if (-not $req) { Write-QLog "SKIP $($f.Name): 读不懂"; continue }
        if ((Get-Date) - $f.LastWriteTime -gt (New-TimeSpan -Minutes 10)) { Write-QLog "SKIP $($req.script): 已过期"; continue }
        $name = [string]$req.script
        if (-not $allowed.ContainsKey($name) -or $name -ne [IO.Path]::GetFileName($name)) { Write-QLog "DENY ${name}: 不在白名单里"; continue }
        $path = Join-Path $PSScriptRoot $name
        if (-not (Test-Path $path)) { Write-QLog "SKIP ${name}: 文件不存在"; continue }
        $params = @{}
        if ($req.args) {
            foreach ($p in $req.args.PSObject.Properties) {
                if ($allowed[$name] -notcontains $p.Name) { continue }
                $v = $p.Value
                if ($v -is [bool]) { if ($v) { $params[$p.Name] = [switch]$true } } else { $params[$p.Name] = [string]$v }
            }
        }
        $desc = ($params.GetEnumerator() | ForEach-Object { "-$($_.Key) $($_.Value)" }) -join ' '
        if ($desc.Length -gt 120) { $desc = $desc.Substring(0, 120) + '…' }
        try {
            $out = & $path @params 2>&1 | Out-String
            Write-QLog ("DONE $name $desc → exit {0} {1}" -f $LASTEXITCODE, ($out.Trim() -replace '\s+', ' '))
        } catch { Write-QLog "FAIL $name $desc → $($_.Exception.Message)" }
    }
}

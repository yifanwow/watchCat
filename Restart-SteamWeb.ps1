# 只重启 Steam 的网页界面（steamwebhelper），不碰 Steam 本身：下载、正在运行的程序、登录状态都不受影响。
# Steam 发现网页进程没了会自己重新拉起；这里确认 Steam 主进程还是原来那个、网页进程已经回来。
# 不需要管理员权限。看门狗和监控窗口的规则 "steam-webhelper" 会调用它（只在电脑空闲、没在玩游戏时）。
param([switch]$Quiet)
$steam = Get-Process steam -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $steam) { Write-Host 'Steam is not running'; exit 0 }
$before = [int]((Get-Process steamwebhelper -ErrorAction SilentlyContinue | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB)
Get-Process steamwebhelper -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$back = $false
for ($i = 0; $i -lt 45; $i++) { Start-Sleep -Seconds 2; if (Get-Process steamwebhelper -ErrorAction SilentlyContinue) { $back = $true; break } }
$same = [bool](Get-Process -Id $steam.Id -ErrorAction SilentlyContinue)
Start-Sleep -Seconds 10   # 等网页界面加载完再量
$after = [int]((Get-Process steamwebhelper -ErrorAction SilentlyContinue | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB)
Write-Host ("Steam web helper restarted: {0} MB -> {1} MB, web helper back: {2}, Steam itself untouched: {3}" -f $before, $after, $back, $same)
if (-not $back -or -not $same) { exit 1 }

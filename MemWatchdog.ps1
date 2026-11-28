# Memory-leak watchdog + history recorder for the WatchDog monitor window.
# Runs as a scheduled task every 10 minutes, takes a few seconds, then exits.
# It never stays resident, so it cannot leak memory itself.
# What to check and how to fix it comes from rules.json (shared) + rules.local.json (your own, not in git).
#   -DryRun      : only print what it would do, write nothing
#   -CollectOnly : record history, but never fix anything
param([switch]$DryRun, [switch]$CollectOnly)
$sw = [Diagnostics.Stopwatch]::StartNew()
$noAct = $DryRun -or $CollectOnly
$HistoryDays = 7

$log = Join-Path $PSScriptRoot 'MemWatchdog.log'
function Log($msg) {
    $tag = if ($DryRun) { '[dry-run] ' } elseif ($CollectOnly) { '[check-only] ' } else { '' }
    $line = '{0:yyyy-MM-dd HH:mm:ss} {1}{2}' -f (Get-Date), $tag, $msg
    Write-Host $line
    if (-not $noAct) { Add-Content -Path $log -Value $line -Encoding UTF8 }
}
function PrivMB($procs) { [int](($procs | Measure-Object PrivateMemorySize64 -Sum).Sum / 1MB) }

# rules.local.json can override a shared rule by using the same id (the WatchDog window's rule editor writes it
# that way: changed limit / idle time / auto on-off), and "disabled": true turns a rule off.
function Load-Rules {
    $byId = [ordered]@{}; $n = 0
    # data\rules.remote.json = the shared rules downloaded by the WatchDog window (once a day, from GitHub).
    # A downloaded rule replaces a bundled one only when its "rev" is higher; one whose fix script is missing here is skipped.
    foreach ($f in 'rules.json', 'data\rules.remote.json', 'rules.local.json') {
        $p = Join-Path $PSScriptRoot $f
        if (-not (Test-Path $p)) { continue }
        $remote = $f -like '*remote*'
        # PowerShell 5.1 returns a JSON array as ONE object; foreach unrolls it into single rules
        try { foreach ($r in (ConvertFrom-Json (Get-Content $p -Raw -Encoding UTF8))) {
            $k = if ($r.id) { [string]$r.id } else { "#" + ($n++) }
            if ($remote) {
                if ($byId.Contains($k) -and [double]$r.rev -le [double]$byId[$k].rev) { continue }
                if ($r.fix.type -eq 'script' -and -not ($r.fix.script -and (Test-Path (Join-Path $PSScriptRoot $r.fix.script)))) { continue }
            }
            $byId[$k] = $r } }
        catch { Log ("RULES ERROR in {0}: {1}" -f $f, $_.Exception.Message) }
    }
    @($byId.Values | Where-Object { -not $_.disabled })
}

# ignore.json (written by the WatchDog window): problems the user chose to ignore for 24 h or confirmed as normal.
# A rule whose key "rule:<id>" is ignored is not fixed automatically.
function Load-Ignored {
    $set = @{}
    $p = Join-Path $PSScriptRoot 'ignore.json'
    if (-not (Test-Path $p)) { return $set }
    try {
        foreach ($i in (ConvertFrom-Json (Get-Content $p -Raw -Encoding UTF8))) {
            if ($i.type -eq 'snooze' -and [datetime]$i.until -lt (Get-Date)) { continue }
            $set[$i.key] = $i
        }
    } catch { Log ("IGNORE FILE ERROR: {0}" -f $_.Exception.Message) }
    $set
}

# Current private memory of a rule's process ("svc:Name" = the process hosting that service). $null = not running.
function Get-TargetMB($name) {
    if ($name -like 'svc:*') {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$($name.Substring(4))'" -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.ProcessId -eq 0) { return $null }
        $p = Get-Process -Id $svc.ProcessId -ErrorAction SilentlyContinue
        if (-not $p) { return $null }
        return PrivMB $p
    }
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if (-not $procs) { return $null }
    PrivMB $procs
}

# Largest handle count among the rule's processes ("svc:Name" = the process hosting that service). $null = not running.
function Get-TargetHandles($name) {
    if ($name -like 'svc:*') {
        $svc = Get-CimInstance Win32_Service -Filter "Name='$($name.Substring(4))'" -ErrorAction SilentlyContinue
        if (-not $svc -or $svc.ProcessId -eq 0) { return $null }
        $p = Get-Process -Id $svc.ProcessId -ErrorAction SilentlyContinue
        if (-not $p) { return $null }
        return $p.HandleCount
    }
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if (-not $procs) { return $null }
    ($procs | Measure-Object HandleCount -Maximum).Maximum
}

# Returns a description of why the rule matched, or $null. All conditions in "when" must hold.
function Test-Rule($r) {
    $w = $r.when; $why = @()
    if ($w.process) {
        $mb = Get-TargetMB $w.process
        if ($null -eq $mb) { return $null }
        if ($w.minMB) { if ($mb -lt $w.minMB) { return $null }; $why += ('{0} {1} MB >= {2} MB' -f $w.process, $mb, $w.minMB) }
        # handles: the single process with the most handles (only one of the 4 nvcontainer processes leaks)
        if ($w.minHandles) {
            $hc = Get-TargetHandles $w.process
            if ($null -eq $hc -or $hc -lt $w.minHandles) { return $null }
            $why += ('{0} {1} handles >= {2}' -f $w.process, $hc, $w.minHandles)
        }
    }
    if ($w.file) {
        if (-not (Test-Path -LiteralPath $w.file)) { return $null }
        $fmb = [int]((Get-Item -LiteralPath $w.file).Length / 1MB)
        if ($w.minFileMB -and $fmb -lt $w.minFileMB) { return $null }
        $why += ('{0} {1} MB >= {2} MB' -f (Split-Path $w.file -Leaf), $fmb, $w.minFileMB)
    }
    if (-not $why) { return $null }
    $why -join ', '
}

# Restart a program the way it is normally started: same path, and the start arguments from the rule
# (e.g. G HUB's tray starts with --minimized at logon; without it a window would pop up).
function Restart-ProcessByName($name, $startArgs) {
    $procs = @(Get-Process -Name $name -ErrorAction SilentlyContinue)
    if (-not $procs) { return }
    $path = ($procs | Where-Object Path | Select-Object -First 1).Path
    $procs | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 5
    if ($path -and -not (Get-Process -Name $name -ErrorAction SilentlyContinue)) {
        if ($startArgs) { Start-Process -FilePath $path -ArgumentList $startArgs -WorkingDirectory (Split-Path $path) }
        else { Start-Process -FilePath $path -WorkingDirectory (Split-Path $path) }
    }
}

function Invoke-Fix($r) {
    switch ($r.fix.type) {
        'restartProcess' { Restart-ProcessByName $r.when.process $r.fix.args }
        'restartService' { & (Join-Path $PSScriptRoot 'Restart-ServiceHard.ps1') -Name $r.fix.service | Out-Null }
        'script'         { & (Join-Path $PSScriptRoot $r.fix.script) -Quiet | Out-Null }
        'restartGroup'   { & (Join-Path $PSScriptRoot 'Restart-Group.ps1') -Steps $r.fix.steps -Quiet | Out-Null }
    }
}

# ---- Restart only at a good moment ----
# "idleMin": no keyboard / mouse input for this many minutes (a light flicker or a 2-second sound gap goes unnoticed).
# "skipIfGame": not while a game started by Steam is running.
function Get-IdleMinutes {
    if (-not ('WdNs.WdIdle' -as [type])) {
        Add-Type -Namespace WdNs -Name WdIdle -MemberDefinition @"
[StructLayout(LayoutKind.Sequential)] public struct LII { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LII p);
public static double Minutes() { LII l = new LII(); l.cbSize = 8; if (!GetLastInputInfo(ref l)) return 0; return (unchecked((uint)System.Environment.TickCount - l.dwTime)) / 60000.0; }
"@
    }
    [WdNs.WdIdle]::Minutes()
}
# A game = something Steam started from steamapps, except always-on Steam apps (desktop pets, wallpapers, cursors)
# listed in the rule's "notGames"
function Test-SteamGame($notGames) {
    $steam = Get-Process steam -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $steam) { return $false }
    $skip = @('steamwebhelper', 'steamservice', 'gameoverlayui', 'gameoverlayui64', 'steamerrorreporter', 'steamerrorreporter64', 'crashhandler', 'crashhandler64') + @($notGames)
    $kids = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($steam.Id)" -ErrorAction SilentlyContinue |
        Where-Object { $skip -notcontains [IO.Path]::GetFileNameWithoutExtension($_.Name) })
    $kids.Count -gt 0
}

# ---- After a restart: check that things are back the way they were ("verify" in the rule) ----
#   running   : these programs must be running again
#   service   : the restarted service must be Running (checked automatically for restartService)
#   file      : this log file (or the newest file in this folder) must be written again after the restart,
#               optionally containing one of "text" (e.g. iCUE reconnected, the lighting effect is drawing frames)
#   soft      : a missing log update is only a warning (e.g. a static lighting effect writes no frames)
# If something is missing after "withinSec", it is recovered once (service started again / program started again
# with its usual arguments) and checked again.
function Get-LogFile($path) {
    if (Test-Path -LiteralPath $path -PathType Container) { Get-ChildItem -LiteralPath $path -File | Sort-Object LastWriteTime | Select-Object -Last 1 }
    else { Get-Item -LiteralPath $path -ErrorAction SilentlyContinue }
}
# Only what was written AFTER the restart counts: $mark = the log file's name and length just before the restart
function Test-FileSince($path, $texts, $since, $mark) {
    $f = Get-LogFile $path
    if (-not $f -or $f.LastWriteTime -lt $since) { return $false }
    if (-not $texts) { return $true }
    $start = if ($mark -and $mark.Name -eq $f.FullName -and $f.Length -ge $mark.Length) { $mark.Length } else { 0 }
    try {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        try { [void]$fs.Seek($start, 'Begin'); $new = (New-Object IO.StreamReader($fs)).ReadToEnd() } finally { $fs.Dispose() }
    } catch { return $false }
    foreach ($t in $texts) { if ($new.Contains($t)) { return $true } }
    $false
}
function Get-VerifyState($r, $t0, $mark) {
    $v = $r.verify; $miss = @()
    foreach ($n in @($v.running)) { if ($n -and -not (Get-Process -Name $n -ErrorAction SilentlyContinue)) { $miss += $n } }
    # services that must be running again: the restarted one, or every service in a restartGroup
    $svcs = @(); if ($r.fix.type -eq 'restartService') { $svcs += $r.fix.service }
    if ($r.fix.type -eq 'restartGroup') { $svcs += @($r.fix.steps | Where-Object { $_.service } | ForEach-Object { $_.service }) }
    $svcDown = @($svcs | Where-Object { $_ -and (Get-Service -Name $_ -ErrorAction SilentlyContinue).Status -ne 'Running' })
    $fileOk = (-not $v.file) -or (Test-FileSince $v.file @($v.text) $t0 $mark)
    [pscustomobject]@{ Missing = $miss; SvcDown = $svcDown; FileOk = $fileOk }
}
function Confirm-Fix($r, $t0, $paths, $mark) {
    $v = $r.verify
    if (-not $v -and $r.fix.type -ne 'restartService' -and $r.fix.type -ne 'restartGroup') { return 'no check defined' }
    if (-not $v) { $v = [pscustomobject]@{}; $r | Add-Member -NotePropertyName verify -NotePropertyValue $v -Force }
    $within = if ($v.withinSec) { [int]$v.withinSec } else { 60 }
    for ($round = 1; $round -le 2; $round++) {
        $deadline = (Get-Date).AddSeconds($within)
        do {
            $st = Get-VerifyState $r $t0 $mark
            if (-not $st.Missing -and -not $st.SvcDown -and $st.FileOk) { return $(if ($round -eq 1) { 'checked: all back to normal' } else { 'checked: back to normal after one recovery' }) }
            Start-Sleep -Seconds 5
        } while ((Get-Date) -lt $deadline)
        if ($round -eq 2) { break }
        # recover once
        foreach ($sv in $st.SvcDown) { Start-Service -Name $sv -ErrorAction SilentlyContinue }
        foreach ($n in $st.Missing) {
            $pp = $paths[$n]
            if ($pp) { $a = $null; if ($r.fix.args -and $n -eq $r.when.process) { $a = $r.fix.args }; if ($a) { Start-Process -FilePath $pp -ArgumentList $a } else { Start-Process -FilePath $pp } }
        }
        if (-not $st.Missing -and -not $st.SvcDown -and -not $st.FileOk -and $v.soft) { return 'checked: running; log not updated (normal if the effect is static)' }
    }
    $msg = @()
    if ($st.Missing) { $msg += ('not running: ' + ($st.Missing -join ', ')) }
    if ($st.SvcDown) { $msg += ('service not running: ' + ($st.SvcDown -join ', ')) }
    if (-not $st.FileOk) { $msg += ('log not updated: ' + $v.file) }
    'CHECK FAILED - ' + ($msg -join '; ')
}

# ---- Check every rule ----
$rules = @(Load-Rules)
$ignored = Load-Ignored
$fixedTargets = @{}
foreach ($r in $rules) {
    $why = Test-Rule $r
    if (-not $why) { Write-Host ("ok   {0}" -f $r.id); continue }
    if ($ignored.ContainsKey("rule:$($r.id)")) { Write-Host ("skip {0}: ignored by user ({1})" -f $r.id, $why); continue }
    if (-not $r.auto -or $r.fix.type -eq 'none') { Log ("NOTICE {0}: {1} ({2})" -f $r.id, $r.title, $why); continue }
    # one fix per target per run (e.g. the NVIDIA database rebuild already restarts the container)
    if ($fixedTargets.ContainsKey($r.target)) { Write-Host ("skip {0}: {1} already fixed this run" -f $r.id, $r.target); continue }
    if ($r.idleMin -and -not $noAct) {
        $idle = Get-IdleMinutes
        if ($idle -lt [double]$r.idleMin) { Write-Host ("wait {0}: in use (idle {1:N0} of {2} min), next run" -f $r.id, $idle, $r.idleMin); continue }
    }
    if ($r.skipIfGame -and -not $noAct -and (Test-SteamGame $r.notGames)) { Write-Host ("wait {0}: a game is running, next run" -f $r.id); continue }
    Log ("FIX {0}: {1} ({2}) -> {3}" -f $r.id, $r.title, $why, $r.fix.label)
    if ($noAct) { continue }
    # remember where the programs that must come back live, to start them again if needed
    $paths = @{}
    foreach ($n in @($r.verify.running) + @($r.when.process)) { if ($n -and $n -notlike 'svc:*') { $pp = (Get-Process -Name $n -ErrorAction SilentlyContinue | Where-Object Path | Select-Object -First 1).Path; if ($pp) { $paths[$n] = $pp } } }
    $before = if ($r.when.process) { Get-TargetMB $r.when.process } else { $null }
    $mark = $null; if ($r.verify.file) { $lf = Get-LogFile $r.verify.file; if ($lf) { $mark = [pscustomobject]@{ Name = $lf.FullName; Length = $lf.Length } } }
    $t0 = Get-Date
    try {
        Invoke-Fix $r; $fixedTargets[$r.target] = 1
        $check = Confirm-Fix $r $t0 $paths $mark
        $after = if ($r.when.process) { Get-TargetMB $r.when.process } else { $null }
        $b1 = if ($null -ne $before) { $before } else { '?' }; $a1 = if ($null -ne $after) { $after } else { '?' }
        Log ("DONE {0}: {1} MB -> {2} MB, {3}" -f $r.id, $b1, $a1, $check)
    } catch { Log ("  failed: {0}" -f $_.Exception.Message) }
}

# ---- History for the WatchDog window: one small CSV per day, old days deleted ----
if ($DryRun) { return }
$dataDir = Join-Path $PSScriptRoot 'data'
if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
$stamp = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
$day   = (Get-Date).ToString('yyyyMMdd')

# Per program (grouped by name): top 30 by memory + top 10 by handle count + every process named in a rule
$all = Get-Process

# ---- Handle surge: record the scene (Capture-Handles.ps1 writes data\incidents\*.txt) ----
# A process whose handle count rose in two runs in a row, and a lot in the last 10 minutes, gets its handles dumped:
# which registry keys / which module's threads it is holding. One capture per program per hour, at most 2 per run.
$statePath = Join-Path $dataDir 'handle-state.csv'
$prevH = @{}
if (Test-Path $statePath) { try { foreach ($r in (Import-Csv $statePath)) { $prevH[[int]$r.Pid] = $r } } catch {} }
$kNow = Get-CimInstance Win32_PerfRawData_PerfOS_Memory -ErrorAction SilentlyContinue
$poolNow = if ($kNow) { [int]($kNow.PoolPagedBytes / 1MB) } else { 0 }
$hState = @(); $surge = @()
foreach ($p in $all) {
    $h = $p.HandleCount
    if ($h -lt 1000) { continue }
    $o = $prevH[$p.Id]
    $same = $o -and $o.Name -eq $p.ProcessName
    $h1 = if ($same) { [int]$o.H } else { -1 }    # last run
    $h0 = if ($same) { [int]$o.H1 } else { -1 }   # the run before
    $hState += [pscustomobject]@{ Pid = $p.Id; Name = $p.ProcessName; H = $h; H1 = $h1; Pool = $poolNow }
    if ($h1 -lt 0 -or $h0 -lt 0) { continue }
    $g = $h - $h1; $g0 = $h1 - $h0; $kg = $poolNow - [int]$o.Pool
    if ($h -ge 3000 -and $g0 -ge 300 -and ($g -ge 2000 -or ($g -ge 1000 -and $g -ge 0.25 * $h1) -or ($kg -ge 50 -and $g -ge 500))) {
        $surge += [pscustomobject]@{ P = $p; G = $g; G0 = $g0; KG = $kg }
    }
}
$hState | Export-Csv -Path $statePath -NoTypeInformation -Encoding UTF8
$incDir = Join-Path $dataDir 'incidents'
foreach ($sg in ($surge | Sort-Object G -Descending | Select-Object -First 2)) {
    $safe = ($sg.P.ProcessName -replace '[^\w\.\-]', '_')
    if (Test-Path $incDir) {
        $recent = Get-ChildItem $incDir -Filter "*-$safe.txt" -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddHours(-1) }
        if ($recent) { continue }
    }
    $why = '看门狗自动记录：句柄 10 分钟 +{0:N0}（再之前 10 分钟 +{1:N0}），现在 {2:N0}；内核分页池 10 分钟 {3:+#,0;-#,0;0} MB' -f $sg.G, $sg.G0, $sg.P.HandleCount, $sg.KG
    try { & (Join-Path $PSScriptRoot 'Capture-Handles.ps1') -Id $sg.P.Id -Reason $why | Out-Null } catch { Log ("CAPTURE failed for {0}: {1}" -f $sg.P.ProcessName, $_.Exception.Message) }
}
$groups = foreach ($g in ($all | Group-Object ProcessName)) {
    $cpuSec = 0.0; $handles = 0
    foreach ($p in $g.Group) { try { $cpuSec += $p.TotalProcessorTime.TotalSeconds } catch {}; $handles += $p.HandleCount }
    [pscustomobject]@{ Time = $stamp; Name = $g.Name; PrivMB = PrivMB $g.Group; Count = $g.Count; CpuSec = [math]::Round($cpuSec, 1); Handles = $handles }
}
$ruleProcs = @($rules | ForEach-Object { $_.when.process } | Where-Object { $_ } | Sort-Object -Unique)
$rows = @($groups | Sort-Object PrivMB -Descending | Select-Object -First 30)
$have = @($rows | ForEach-Object { $_.Name })
$rows += @($groups | Where-Object { $ruleProcs -contains $_.Name -and $have -notcontains $_.Name })
$have = @($rows | ForEach-Object { $_.Name })
$rows += @($groups | Where-Object { $have -notcontains $_.Name } | Sort-Object Handles -Descending | Select-Object -First 10)
# Services hidden inside svchost etc. get their own row: svc:<name>
foreach ($n in ($ruleProcs | Where-Object { $_ -like 'svc:*' })) {
    $mb = Get-TargetMB $n
    if ($null -ne $mb) { $rows += [pscustomobject]@{ Time = $stamp; Name = $n; PrivMB = $mb; Count = 1; CpuSec = 0; Handles = 0 } }
}
# procv2 = proc + Handles column (a leak of handles shows up as a steady climb over hours)
$rows | Export-Csv -Path (Join-Path $dataDir "procv2-$day.csv") -Append -NoTypeInformation -Encoding UTF8

# Whole system
$os  = Get-CimInstance Win32_OperatingSystem
$cpu = [int](Get-CimInstance Win32_Processor | Measure-Object LoadPercentage -Average).Average
$nvDb = 'C:\ProgramData\NVIDIA Corporation\nvtopps\nvtopps.db3'
$dbNow = if (Test-Path $nvDb) { [int]((Get-Item $nvDb).Length / 1MB) } else { 0 }
$disk = Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter "Name='_Total'" -ErrorAction SilentlyContinue
# Kernel memory pools: a driver leak shows up as paged / nonpaged pool growing for hours, invisible in the process list
$kmem = Get-CimInstance Win32_PerfRawData_PerfOS_Memory -ErrorAction SilentlyContinue
# sysv4 = sys + disk + the watchdog's own CPU time and peak memory + kernel pools and total handle count
# (new file name each time columns change, because Export-Csv can't append rows with new columns to an old file)
$self = Get-Process -Id $PID
[pscustomobject]@{
    Time          = $stamp
    UsedMB        = [int](($os.TotalVisibleMemorySize - $os.FreePhysicalMemory) / 1KB)
    TotalMB       = [int]($os.TotalVisibleMemorySize / 1KB)
    CommitMB      = [int](($os.TotalVirtualMemorySize - $os.FreeVirtualMemory) / 1KB)
    CommitLimitMB = [int]($os.TotalVirtualMemorySize / 1KB)
    CpuPct        = $cpu
    NvToppsMB     = $dbNow
    DiskReadMBs   = if ($disk) { [math]::Round($disk.DiskReadBytesPersec / 1MB, 2) } else { 0 }
    DiskWriteMBs  = if ($disk) { [math]::Round($disk.DiskWriteBytesPersec / 1MB, 2) } else { 0 }
    SelfCpuSec    = [math]::Round($self.TotalProcessorTime.TotalSeconds, 2)
    SelfPeakMB    = [int]($self.PeakWorkingSet64 / 1MB)
    PoolPagedMB    = if ($kmem) { [int]($kmem.PoolPagedBytes / 1MB) } else { 0 }
    PoolNonpagedMB = if ($kmem) { [int]($kmem.PoolNonpagedBytes / 1MB) } else { 0 }
    Handles        = [int](($all | Measure-Object HandleCount -Sum).Sum)
    RunMs         = $sw.ElapsedMilliseconds
} | Export-Csv -Path (Join-Path $dataDir "sysv4-$day.csv") -Append -NoTypeInformation -Encoding UTF8

Get-ChildItem $dataDir -Filter '*.csv' | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-$HistoryDays) } | Remove-Item -Force -ErrorAction SilentlyContinue

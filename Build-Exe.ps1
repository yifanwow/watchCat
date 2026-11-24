# Build WatchDog.exe: the monitor window, written in C# (src\*.cs).
# Uses the C# compiler that ships with Windows (.NET Framework 4), so nothing is downloaded.
# The exe must stay in this folder: it reads rules.json / rules.local.json / data\ here,
# and runs the PowerShell helper scripts (watchdog, fixes) from here.
Add-Type -AssemblyName System.Drawing
$dir = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { Write-Host 'csc.exe (.NET Framework 4) not found'; exit 1 }

# ---- Icon: the WatchCat pixel logo (assets\mascot\watchcat-logo-1536.png = a 48x48 pixel grid drawn at 32 px per pixel) ----
# Read the grid back as a crisp 48x48 image; big icon sizes are whole-number enlargements of it (stays pixel-sharp),
# 32 / 24 / 16 are smooth reductions of the big picture. The 48x48 grid is also embedded in the exe for the title bar.
$logoSrc = Join-Path $dir 'assets\mascot\watchcat-logo-1536.png'
if (-not (Test-Path $logoSrc)) { Write-Host "logo not found: $logoSrc"; exit 1 }
$big = [Drawing.Bitmap]::FromFile($logoSrc)
$grid = New-Object Drawing.Bitmap(48, 48)
$cell = $big.Width / 48
for ($y = 0; $y -lt 48; $y++) { for ($x = 0; $x -lt 48; $x++) { $grid.SetPixel($x, $y, $big.GetPixel([int]($x * $cell + $cell / 2), [int]($y * $cell + $cell / 2))) } }
$logo48 = Join-Path $env:TEMP 'WatchCat.logo48.png'
$grid.Save($logo48, [Drawing.Imaging.ImageFormat]::Png)
function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap($s, $s)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $n = [math]::Floor($s / 48)
        if ($n -ge 1) {
            # whole-number enlargement of the pixel grid, centered
            $g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
            $o = [int](($s - 48 * $n) / 2)
            $g.DrawImage($grid, (New-Object Drawing.Rectangle($o, $o, (48 * $n), (48 * $n))))
        } else {
            $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
            $g.DrawImage($big, (New-Object Drawing.Rectangle(0, 0, $s, $s)))
        }
        $ms = New-Object IO.MemoryStream
        $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    } finally { $g.Dispose(); $bmp.Dispose() }
}
$sizes = 256, 96, 48, 32, 24, 16
$pngs = @(foreach ($s in $sizes) { , (New-IconPng $s) })
$big.Dispose(); $grid.Dispose()
$ico = Join-Path $dir 'WatchDog.ico'
$fs = [IO.File]::Create($ico); $bw = New-Object IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
        $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
        $offset += $pngs[$i].Length
    }
    foreach ($p in $pngs) { $bw.Write($p) }
} finally { $bw.Close() }

# ---- Manifest: modern controls, sharp text on high-DPI screens, Windows 10/11 ----
$manifest = Join-Path $env:TEMP 'WatchDog.manifest'
@'
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="WatchDog"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2"><security><requestedPrivileges xmlns="urn:schemas-microsoft-com:asm.v3">
    <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
  </requestedPrivileges></security></trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1"><application>
    <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}"/>
  </application></compatibility>
  <application xmlns="urn:schemas-microsoft-com:asm.v3"><windowsSettings>
    <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true</dpiAware>
  </windowsSettings></application>
</assembly>
'@ | Set-Content $manifest -Encoding UTF8

$exe = Join-Path $dir 'WatchDog.exe'
$src = @(Get-ChildItem (Join-Path $dir 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
if (-not $src) { Write-Host 'src\*.cs not found'; exit 1 }
# The monitor may be running (it hides in the tray): close it first, the exe file is locked while it runs
$running = @(Get-Process WatchDog -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
$wasRunning = $running.Count -gt 0
if ($wasRunning) {
    # Ask it to save its live data and exit (WatchDog.exe --exit), then wait for it
    Start-Process $exe -ArgumentList '--exit' -Wait
    foreach ($p in $running) { $p.WaitForExit(5000) | Out-Null }
    if (@(Get-Process WatchDog -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) { Write-Host 'WatchDog is still running - exit it from the tray icon first (right-click -> 退出).'; exit 1 }
}
& $csc /nologo /target:winexe /optimize+ /codepage:65001 "/win32icon:$ico" "/win32manifest:$manifest" "/out:$exe" "/resource:$logo48,WatchCat.logo48.png" `
    /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Core.dll `
    /reference:System.Web.Extensions.dll /reference:Microsoft.VisualBasic.dll $src
$ok = $LASTEXITCODE -eq 0
Remove-Item $manifest, $logo48 -ErrorAction SilentlyContinue
if ($ok -and (Test-Path $exe)) { Write-Host "Built: $exe" } else { Write-Host 'Build failed'; exit 1 }
# it was running before: start the new version. Through explorer.exe, so it is not a child of whatever ran this
# script (e.g. a Store app like Claude desktop) - those kill their whole process tree when they update
if ($wasRunning) { Start-Process explorer.exe -ArgumentList "`"$exe`"" }

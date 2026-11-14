# End-to-end checks against the real Perch.exe on this desktop.
#   powershell -ExecutionPolicy Bypass -File build/e2e.ps1 [-Exe path\Perch.exe] [-Installer dist\Perch-Setup-x.exe]
# Moves the mouse to the top of the primary screen for a few seconds (hover test). Uses throwaway data folders;
# never touches %APPDATA%\Perch. The installer test installs into a temp folder and uninstalls again.
param(
    [string]$Exe = (Join-Path $PSScriptRoot "..\src\Perch\bin\Debug\net8.0-windows10.0.19041.0\Perch.exe"),
    [string]$Installer = "",
    [string]$OutDir = (Join-Path $PSScriptRoot "..\dist\e2e")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace E2E -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
public struct POINT { public int X; public int Y; }
'@

$Exe = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$results = New-Object System.Collections.Generic.List[object]
function Check([string]$name, [bool]$ok, [string]$detail = "") {
    $results.Add([pscustomobject]@{ Result = $(if ($ok) { "PASS" } else { "FAIL" }); Check = $name; Detail = $detail })
    $color = if ($ok) { "Green" } else { "Red" }
    Write-Host ("[{0}] {1} {2}" -f $(if ($ok) { "PASS" } else { "FAIL" }), $name, $detail) -ForegroundColor $color
}
function NewData { $d = Join-Path $env:TEMP ("perch-e2e-" + [guid]::NewGuid().ToString("N")); New-Item -ItemType Directory $d | Out-Null; $d }
function RunPerch([string[]]$PerchArgs, [int]$TimeoutSec = 30) {
    $p = Start-Process -FilePath $Exe -ArgumentList $PerchArgs -PassThru
    if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); return $false }
    return $true
}
function ColorCount([string]$png) {
    $bmp = [System.Drawing.Bitmap]::FromFile($png)
    try {
        $set = New-Object System.Collections.Generic.HashSet[int]
        for ($y = 0; $y -lt $bmp.Height; $y += 3) { for ($x = 0; $x -lt $bmp.Width; $x += 3) { [void]$set.Add($bmp.GetPixel($x, $y).ToArgb()) } }
        return $set.Count
    } finally { $bmp.Dispose() }
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cx = $screen.X + [int]($screen.Width / 2)

# ---------------------------------------------------------------- 1. pages discovered
$data = NewData
$list = Join-Path $data "pages.txt"
$ok = RunPerch @("--data-dir", $data, "--list-pages", $list)
$pages = if (Test-Path $list) { Get-Content $list | ForEach-Object { ($_ -split "`t")[0] } } else { @() }
$expected = "media", "today", "shelf", "tasks", "focus", "ai", "prayer", "system"
$missing = $expected | Where-Object { $pages -notcontains $_ }
Check "all pages discovered" ($ok -and -not $missing) ("found: " + ($pages -join ", ") + $(if ($missing) { "; missing: " + ($missing -join ", ") } else { "" }))

# ---------------------------------------------------------------- 2. every page renders in every theme
foreach ($theme in "Midnight", "Daylight", "Smoke", "Aurora") {
    foreach ($page in $pages) {
        $png = Join-Path $OutDir ("{0}-{1}.png" -f $page, $theme.ToLower())
        if (Test-Path $png) { Remove-Item $png }
        $ok = RunPerch @("--data-dir", $data, "--theme", $theme, "--expand", $page, "--screenshot", $png, "--delay", "1600")
        $colors = if (Test-Path $png) { ColorCount $png } else { 0 }
        Check "render $page ($theme)" ($ok -and $colors -gt 60) "$colors colours"
    }
}
foreach ($shape in "Docked", "Floating") {
    $png = Join-Path $OutDir ("rest-{0}.png" -f $shape.ToLower())
    $ok = RunPerch @("--data-dir", $data, "--shape", $shape, "--notify", "Hello from Perch", "--screenshot", $png, "--delay", "1200")
    Check "resting notice ($shape)" ($ok -and (Test-Path $png) -and (ColorCount $png) -gt 20)
}

# ---------------------------------------------------------------- 3. live: hover opens, leaving closes, click-through
function ClickTargetIsPerch([int]$X, [int]$Y, [int]$PerchPid, [int]$SettleMs = 150) {
    # Move the pointer there, let Perch's hit-test poll react, then ask Windows which window a click would hit.
    [void][E2E.Win]::SetCursorPos($X, $Y)
    Start-Sleep -Milliseconds $SettleMs
    $pt = New-Object E2E.Win+POINT; $pt.X = $X; $pt.Y = $Y
    $root = [E2E.Win]::GetAncestor([E2E.Win]::WindowFromPoint($pt), 2)
    $owner = [uint32]0; [void][E2E.Win]::GetWindowThreadProcessId($root, [ref]$owner)
    return ($owner -eq $PerchPid)
}

$liveData = NewData
# Keep the resting island to the plain clock so its geometry is predictable even if music is playing.
New-Item -ItemType Directory -Force (Join-Path $liveData "data") | Out-Null
'{ "show_media_in_pill": false, "theme": "midnight", "hover_delay_ms": 400 }' | Set-Content (Join-Path $liveData "data\settings.json") -Encoding utf8
$p = Start-Process -FilePath $Exe -ArgumentList @("--data-dir", $liveData, "--theme", "Midnight") -PassThru
Start-Sleep -Milliseconds 2500
$orig = New-Object E2E.Win+POINT; [void][E2E.Win]::GetCursorPos([ref]$orig)
$x = $cx - 40
try {
    [void][E2E.Win]::SetCursorPos($cx, $screen.Y + $screen.Height - 200); Start-Sleep -Milliseconds 600
    # Resting island is 32 DIPs tall; 48 px below the top is inside its drop shadow at 100-125 % scaling.
    $shadow = ClickTargetIsPerch $x ($screen.Y + 48) $p.Id
    Check "click on the shadow under the island falls through" (-not $shadow) ""
    $onIsland = ClickTargetIsPerch $x ($screen.Y + 14) $p.Id 120   # quicker than the 400 ms open delay
    Check "click on the resting island hits the island" $onIsland ""
    Start-Sleep -Milliseconds 1100                                  # pointer stays: the island opens
    $openHit = ClickTargetIsPerch $x ($screen.Y + 200) $p.Id 120   # 200 px down is inside the open panel only
    Check "hover opens the island" $openHit "click 200 px below the top lands on the open panel"
    [void][E2E.Win]::SetCursorPos($cx, $screen.Y + $screen.Height - 200); Start-Sleep -Milliseconds 1600
    $afterLeave = ClickTargetIsPerch $x ($screen.Y + 200) $p.Id 120
    Check "leaving closes it again" (-not $afterLeave) "the same point falls through once closed"

    # Click-through: a point inside Perch's window rectangle but outside the island must belong to another window.
    $pt = New-Object E2E.Win+POINT; $pt.X = $cx + 330; $pt.Y = $screen.Y + 150
    $hwnd = [E2E.Win]::WindowFromPoint($pt)
    $root = [E2E.Win]::GetAncestor($hwnd, 2)
    $pid2 = [uint32]0; [void][E2E.Win]::GetWindowThreadProcessId($root, [ref]$pid2)
    Check "transparent area is click-through" ($pid2 -ne $p.Id) "window under point belongs to pid $pid2 (Perch is $($p.Id))"

    # Idle cost
    $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 5
    $p.Refresh(); $cpu1 = $p.TotalProcessorTime.TotalMilliseconds
    $pct = ($cpu1 - $cpu0) / 5000 / [Environment]::ProcessorCount * 100
    $mb = [math]::Round($p.WorkingSet64 / 1MB)
    Check "idle CPU below 1%" ($pct -lt 1) ("{0:N2} %" -f $pct)
    Check "memory below 250 MB" ($mb -lt 250) "$mb MB"
    $logText = Get-Content (Join-Path $liveData "perch.log") -Raw
    Check "no errors logged" (-not ($logText -match "\[ERROR\]")) ""
} finally {
    [void][E2E.Win]::SetCursorPos($orig.X, $orig.Y)
    if (-not $p.HasExited) { $p.Kill() }
}

# ---------------------------------------------------------------- 3b. cost while the music pill animates
$musicData = NewData
$m = Start-Process -FilePath $Exe -ArgumentList @("--data-dir", $musicData, "--demo-media") -PassThru
try {
    Start-Sleep -Seconds 3
    $m.Refresh(); $c0 = $m.TotalProcessorTime.TotalMilliseconds
    Start-Sleep -Seconds 5
    $m.Refresh(); $c1 = $m.TotalProcessorTime.TotalMilliseconds
    $mpct = ($c1 - $c0) / 5000 / [Environment]::ProcessorCount * 100
    Check "CPU with the music pill animating below 1.5%" ($mpct -lt 1.5) ("{0:N2} %" -f $mpct)
} finally { if (-not $m.HasExited) { $m.Kill() } }

# ---------------------------------------------------------------- 4. single instance (real mutex, private data via env)
$env:PERCH_DATA_DIR = NewData
try {
    $first = Start-Process -FilePath $Exe -PassThru
    Start-Sleep -Seconds 2
    $second = Start-Process -FilePath $Exe -PassThru
    $exited = $second.WaitForExit(5000)
    Check "second copy exits, first keeps running" ($exited -and -not $first.HasExited) ""
} finally {
    if ($first -and -not $first.HasExited) { $first.Kill() }
    Remove-Item Env:\PERCH_DATA_DIR
}

# ---------------------------------------------------------------- 5. installer round trip
if ($Installer) {
    $Installer = (Resolve-Path $Installer).Path
    $dir = Join-Path $env:TEMP ("perch-install-" + [guid]::NewGuid().ToString("N"))
    $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    $hadRun = $null -ne (Get-ItemProperty $runKey -Name Perch -ErrorAction SilentlyContinue)
    $inst = Start-Process -FilePath $Installer -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/DIR=`"$dir`"", "/TASKS=`"`"") -PassThru -Wait
    $exe2 = Join-Path $dir "Perch.exe"
    $lnk = Join-Path ([Environment]::GetFolderPath("Programs")) "Perch\Perch.lnk"
    Check "installer exits 0" ($inst.ExitCode -eq 0) "exit $($inst.ExitCode)"
    Check "installed Perch.exe and uninstaller" ((Test-Path $exe2) -and (Test-Path (Join-Path $dir "unins000.exe"))) $dir
    Check "Start menu shortcut" (Test-Path $lnk) $lnk
    $d2 = NewData; $l2 = Join-Path $d2 "pages.txt"
    $run = Start-Process -FilePath $exe2 -ArgumentList @("--data-dir", $d2, "--list-pages", $l2) -PassThru
    $ranOk = $run.WaitForExit(30000) -and (Test-Path $l2) -and ((Get-Content $l2).Count -ge $expected.Count)
    Check "installed app starts (self-contained, no .NET needed)" $ranOk ""
    $noRun = $hadRun -or ($null -eq (Get-ItemProperty $runKey -Name Perch -ErrorAction SilentlyContinue))
    Check "autostart left off when unticked" $noRun ""
    $un = Start-Process -FilePath (Join-Path $dir "unins000.exe") -ArgumentList @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART") -PassThru -Wait
    Start-Sleep -Seconds 2
    Check "uninstaller removes the app" ((-not (Test-Path $exe2)) -and (-not (Test-Path $lnk))) "exit $($un.ExitCode)"
}

# ---------------------------------------------------------------- summary
$failed = @($results | Where-Object Result -eq "FAIL")
$results | Format-Table -AutoSize | Out-String -Width 200 | Set-Content (Join-Path $OutDir "e2e-results.txt")
Write-Host ""
Write-Host ("{0} checks, {1} failed. Screens and results in {2}" -f $results.Count, $failed.Count, $OutDir) -ForegroundColor $(if ($failed.Count) { "Red" } else { "Green" })
exit $failed.Count

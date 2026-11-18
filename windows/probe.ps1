# A read-only look at the switcher: connects, reads everything it reports, prints it, saves a
# picture of the window, and quits. It refuses every switcher command and reports how many went
# out (always 0), connects no cameras, and writes nothing to the setup file, so it's safe on the
# real switcher, even during a service.
#
#   .\probe.ps1                        the switcher in the setup file (%APPDATA%\CueVideo\config.json)
#   .\probe.ps1 -Switcher 192.168.1.240
#   .\probe.ps1 -App dist\ChurchfrontVideo.exe -Out C:\temp\probe
param([string]$Switcher, [int]$Port = 9910, [string]$App, [string]$Out)
$ErrorActionPreference = 'Stop'
if (-not $App) {
    $App = @("$PSScriptRoot\dist\ChurchfrontVideo.exe", "$PSScriptRoot\ChurchfrontVideo.exe",
             "$PSScriptRoot\src\ChurchfrontVideo\bin\Release\net8.0-windows\win-x64\ChurchfrontVideo.exe",
             "$PSScriptRoot\src\ChurchfrontVideo\bin\Debug\net8.0-windows\ChurchfrontVideo.exe") |
           Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $App -or -not (Test-Path $App)) { throw "ChurchfrontVideo.exe not found. Build it (.\build.ps1) or give -App." }
if (-not $Out) { $Out = Join-Path $env:APPDATA 'CueVideo\Logs\probe' }

$env:CUEVIDEO_PROBE = '1'
$env:CUEVIDEO_PROBE_OUT = $Out
$env:CUEVIDEO_PORT = "$Port"
if ($Switcher) { $env:CUEVIDEO_HOST = $Switcher } else { Remove-Item Env:\CUEVIDEO_HOST -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $Out 'probe.txt') -ErrorAction SilentlyContinue
$p = Start-Process $App -PassThru -Wait
$report = Join-Path $Out 'probe.txt'
if (Test-Path $report) { Get-Content $report } else { "No report was written (exit code $($p.ExitCode))." }
exit $p.ExitCode

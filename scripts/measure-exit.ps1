param(
    [Parameter(Mandatory = $true)][int]$RecorderProcessId,
    [Parameter(Mandatory = $true)][string]$Scenario,
    [string]$Output = "artifacts\regression\1.0.4"
)

# Observe only. Closing is performed through the application's normal UI.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outputPath = Join-Path $root $Output
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$process = [System.Diagnostics.Process]::GetProcessById($RecorderProcessId)
# Retain a process handle before it exits; Get-Process' extended object can
# otherwise lose ExitTime/ExitCode when the PID disappears between reads.
$process.EnableRaisingEvents = $true
$null = $process.Handle
$watchedFrom = [DateTimeOffset]::UtcNow
Write-Host "Watching PID $RecorderProcessId ($Scenario). Close it through the normal UI."
$exited = $process.WaitForExit(60000)
$observedExit = [DateTimeOffset]::UtcNow
$result = [ordered]@{
    scenario = $Scenario
    processId = $RecorderProcessId
    watchedFromUtc = $watchedFrom.ToString("O")
    exited = $exited
    exitCode = $(if ($exited) { $process.ExitCode } else { $null })
    exitUtc = $(if ($exited) { $process.ExitTime.ToUniversalTime().ToString("O") } else { $null })
    observedExitUtc = $observedExit.ToString("O")
}
$json = $result | ConvertTo-Json
[System.IO.File]::WriteAllText((Join-Path $outputPath "$Scenario-exit.json"), $json, [System.Text.UTF8Encoding]::new($false))
Write-Output $json
if (-not $exited) { throw "The recorder did not exit within 60 seconds; it was not terminated by this script." }

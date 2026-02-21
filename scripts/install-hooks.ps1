param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$TargetDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$TargetDir = Resolve-Path $TargetDir

Write-Host "Installing beacon hooks into: $TargetDir"
Write-Host ""

$copilotDir = Join-Path $TargetDir ".github" "hooks"
New-Item -ItemType Directory -Path $copilotDir -Force | Out-Null
Copy-Item (Join-Path $repoRoot "hook-configs" "copilot" "CopilotHookSettings.json") (Join-Path $copilotDir "CopilotHookSettings.json") -Force
Write-Host "[copilot]  Installed .github/hooks/CopilotHookSettings.json"

$claudeDir = Join-Path $TargetDir ".claude"
$claudeSettings = Join-Path $claudeDir "settings.json"
New-Item -ItemType Directory -Path $claudeDir -Force | Out-Null
if (Test-Path $claudeSettings) {
    Write-Host "[claude]   .claude/settings.json already exists - skipping."
    Write-Host "           Manually merge the hooks from: hook-configs/claude-code/settings.json"
}
else {
    Copy-Item (Join-Path $repoRoot "hook-configs" "claude-code" "settings.json") $claudeSettings -Force
    Write-Host "[claude]   Installed .claude/settings.json"
}

Write-Host ""
Write-Host "Done. Make sure the beacon server is running on http://127.0.0.1:17321"

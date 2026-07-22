# Moves the staged agent-setup files into their protected locations.
# (.claude/ and .github/workflows/ can't be written by remote Claude sessions,
# so they're staged here under neutral names — review them, then run this
# script: powershell -File _agent-setup/apply.ps1)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot   # repo root (_agent-setup's parent)

$map = @{
  'claude-config/settings.json'                    = '.claude/settings.json'
  'claude-config/hooks/post-edit-guard.sh'         = '.claude/hooks/post-edit-guard.sh'
  'claude-config/skills/add-install-step/SKILL.md' = '.claude/skills/add-install-step/SKILL.md'
  'claude-config/skills/schema-change/SKILL.md'    = '.claude/skills/schema-change/SKILL.md'
  'claude-config/skills/write-adr/SKILL.md'        = '.claude/skills/write-adr/SKILL.md'
  'claude-config/skills/aot-safety/SKILL.md'       = '.claude/skills/aot-safety/SKILL.md'
  'github-workflows/pr-guards.yml'                 = '.github/workflows/pr-guards.yml'
}

foreach ($src in $map.Keys) {
  $from = Join-Path $PSScriptRoot $src
  $to   = Join-Path $root $map[$src]
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
  Copy-Item -Force $from $to
  Write-Host "applied $($map[$src])"
}
Write-Host "`nDone. Review with 'git status', then delete _agent-setup/ ."

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
git config core.hooksPath .githooks
Write-Host "Git hooks installed: core.hooksPath -> .githooks"

#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regenerates docs/api/ from XML doc comments via docfx.

.DESCRIPTION
    Runs docfx (assumed installed as a global tool: `dotnet tool install -g docfx`)
    against docfx.json at the repo root, producing a static API reference site
    under docs/api/.

    If docfx is not installed, prints install instructions and exits 0
    (this script is opt-in for now; the rest of the docs pipeline doesn't depend
    on it).

.NOTES
    Run from repo root.
#>

[CmdletBinding()]
param(
    [string] $DocfxConfig = 'docfx.json'
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent | Split-Path -Parent)

if (-not (Get-Command docfx -ErrorAction SilentlyContinue)) {
    Write-Host "docfx not found. Install with: dotnet tool install -g docfx"
    Write-Host "(skipping API doc generation; not a hard error)"
    exit 0
}

if (-not (Test-Path $DocfxConfig)) {
    Write-Host "docfx.json not found at $DocfxConfig -- API doc generation not yet wired."
    Write-Host "When ready: run 'docfx init' and commit docfx.json + a metadata stub."
    exit 0
}

& docfx build $DocfxConfig
if ($LASTEXITCODE -ne 0) { throw "docfx build exited $LASTEXITCODE" }

Write-Host "API docs regenerated at docs/api/"

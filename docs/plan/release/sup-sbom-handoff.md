# SUP -> REL handoff: CycloneDX SBOM step for `release.yml` (SUP.3 / R42)

**Status:** written, not applied. Lane SUP's branch (`rc/sup-supply-chain`) was
cut from `release/v0.1.0-alpha` at `b62de86`, *before* lane REL creates
`.github/workflows/release.yml` (Task REL.5). That file does not exist in this
worktree, so SUP cannot append to it without creating a competing copy — which
Stage 3's cross-lane rules explicitly forbid. This doc is the step SUP would
have appended, for REL (or the orchestrator) to fold in during/after REL
merges.

## What to add, and where

Add a new step to the release job in `release.yml`, **after** the AOT-publish
+ Authenticode-sign steps and **before** the `SHA256SUMS` / GitHub Release
attach step (Task REL.5, Step 1) — the SBOM should describe exactly what got
signed and released, so it must run once the publish output is final but
before it's packaged into the release assets.

```yaml
      - name: install CycloneDX SBOM tool
        run: dotnet tool install --global CycloneDX

      - name: generate CycloneDX SBOM
        shell: pwsh
        run: |
          dotnet-cyclonedx Sigil.slnx -o publish/sbom --json -f sigil.cdx.json
          if (-not (Test-Path "publish/sbom/sigil.cdx.json")) { throw "SBOM not generated" }
```

Then include `publish/sbom/sigil.cdx.json` alongside `SHA256SUMS` and
`THIRD-PARTY-NOTICES.md` in the GitHub Release asset list (REL.5, Step 1).

## Notes for REL

- The `CycloneDX` dotnet tool (NuGet package `CycloneDX`, command
  `dotnet-cyclonedx`) walks the `.slnx`/project graph and central package
  versions directly — it does not need a prior `dotnet publish` output, only
  restore. Run it any time after `dotnet restore Sigil.slnx`.
- This was **not run against this repo** in the SUP lane — no `release.yml`
  exists yet to run it in, and running the global tool install + scan
  standalone here would validate the tool but not the actual placement. REL
  should do a real dry run once the step lands in the real workflow.
- Coordinate ownership: `release.yml` is REL's file per Stage 3's cross-lane
  rules. This doc is informational only; SUP has not modified any workflow
  file that REL owns.

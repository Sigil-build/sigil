# Sprint 1 — Identifier Reservation Checklist

Per [decisions.md#D-007](../../product-architect-package/decisions.md) and [decisions.md#D-009](../../product-architect-package/decisions.md). All items must be done by a human (founder) — no automation.

## Day-1 must-do (Sprint 1 exit gate)

- [ ] GitHub org `Sigil-build` exists (already done — verify access)
- [ ] Public repo `Sigil-build/sigil` created with MIT license, default branch `main`
- [ ] Private repo `Sigil-build/cloud` created (empty placeholder, no code)
- [ ] Branch protection on `Sigil-build/sigil:main`:
  - Require PR before merging
  - Require 1 approving review
  - Require status checks: `ci / build (ubuntu-latest)`, `ci / build (windows-latest)`, `ci / build (macos-latest)`, `secret-scan`
  - Require linear history
  - Block force pushes
- [ ] CODEOWNERS file is enforced (set in branch protection "require review from Code Owners")
- [ ] Domain `sigil.build` registered and pointed at placeholder
- [ ] Domain `sigil.me` 301-redirect → `sigil.build`
- [ ] NuGet ID `SigilBuild` reserved (push empty `0.0.0-reserved` package): see "Reservation push" below
- [ ] Defensive registration: `sigil.cloud`, `sigil.dev` (only if free; skip if taken)

## Pre-launch (defer to Sprint 11)

- [ ] Twitter `@sigilbuild`
- [ ] USPTO/EUIPO trademark search for "Sigil" in classes 9 / 42

## Reservation push (manual)

Build the reservation `.nupkg` locally first:

```bash
dotnet pack src/SigilBuild.Cli -c Release \
  -p:Version=0.0.0-reserved \
  -p:PackageDescription="Reserved. The first real release of Sigil ships in v0.1.0+. See https://sigil.build" \
  -o publish/nupkg
```

Inspect `publish/nupkg/SigilBuild.0.0.0-reserved.nupkg` (it's a zip — confirm
`.nuspec` shows `<id>SigilBuild</id>` and `<version>0.0.0-reserved</version>`).

Once the package has been inspected, the founder runs (with their NuGet API key):

```bash
dotnet nuget push publish/nupkg/SigilBuild.0.0.0-reserved.nupkg \
  --api-key $NUGET_API_KEY \
  --source https://api.nuget.org/v3/index.json
```

Verify on <https://www.nuget.org/packages/SigilBuild> that the package is listed
and unlisted (you can unlist it via the NuGet web UI to keep search results
clean — the id stays reserved either way).

## Sign-off

- Reserved by: ______________________
- Date: ______________________

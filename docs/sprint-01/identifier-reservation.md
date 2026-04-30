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
- [ ] NuGet ID `SigilBuild` reserved (push empty `0.0.0-reserved` package): see Task 9
- [ ] Defensive registration: `sigil.cloud`, `sigil.dev` (only if free; skip if taken)

## Pre-launch (defer to Sprint 11)

- [ ] Twitter `@sigilbuild`
- [ ] USPTO/EUIPO trademark search for "Sigil" in classes 9 / 42

## Sign-off

- Reserved by: ______________________
- Date: ______________________

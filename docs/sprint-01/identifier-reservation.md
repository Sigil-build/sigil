# Sprint 1 — Identifier Reservation Checklist

This document records the identifier reservations and access-control settings established at the start of Sprint 1, per Plan _1 Tasks 1 and 9. Items marked "to verify" require a live check against the relevant external registry.

---

## Package Identifiers

### NuGet — tool package

| Item | Value | Status |
|---|---|---|
| Package ID (CLI tool) | `SigilBuild` | ⏳ Reserved placeholder to be published before Sprint 1 ends |
| Install command | `dotnet tool install -g SigilBuild` | — |
| Binary command | `sigil` (via `<ToolCommandName>`) | — |

> The `Sigil` package ID is blocked by Kevin Montrose's IL-emit library (~6M downloads in the same NuGet ecosystem). `SigilBuild` was chosen as the namespace prefix for all public packages (D-007).

### NuGet — Update SDK

| Item | Value | Status |
|---|---|---|
| Package ID | `SigilBuild.UpdateSdk` | ⏳ Pending public reservation — ships post-Sprint 8 |

The Update SDK is a client NuGet consumed by end-user applications. It is not part of Sprint 1 deliverables; the reservation should be made before Sprint 8 to prevent squatting.

---

## Domain Names

| Domain | Purpose | Status |
|---|---|---|
| `sigil.build` | Primary product domain (`docs.sigil.build`, `api.sigil.build`) | ✅ Purchased (D-007) |
| `sigil.me` | Backup domain | ✅ Owned — 301 redirect to `sigil.build` (D-007) |
| `sigil.cloud` | Defensive registration | ☐ To verify / register |
| `sigil.dev` | Defensive registration | ☐ To verify / register |

---

## CLI Binary Name

| Item | Value | Status |
|---|---|---|
| Windows binary | `sigil.exe` | ✅ Reserved via `<ToolCommandName>` in `.csproj` |
| Brand-friendly aliases | None registered yet | ☐ Post-MVP |

---

## GitHub Organization and Repository

| Item | Value | Status |
|---|---|---|
| GitHub org | `Sigil-build` | ✅ Registered (D-007, confirmed via `git remote -v`: `https://github.com/Sigil-build/sigil.git`) |
| Public OSS repo | `Sigil-build/sigil` | ✅ Public from day 1 (D-009) |
| Private SaaS repo | `Sigil-build/cloud` | ✅ Private placeholder created (D-009) |
| CODEOWNERS | Present in OSS repo root | ✅ |

---

## Branch Protection (`main`)

Per D-009, branch protection on `main` was configured at repo creation. Intended settings (to verify via `gh api repos/Sigil-build/sigil/branches/main/protection`):

- Require pull request review before merging (minimum 1 approver).
- Require CI status checks to pass (`build`, `test`, `lint`) before merge.
- Do not allow force-push to `main`.
- Do not allow deletion of `main`.

Status: ⏳ To verify live settings match intent.

---

## License

| Component | License |
|---|---|
| `SigilBuild.Cli`, `SigilBuild.Core`, `SigilBuild.Packaging`, `SigilBuild.Signing`, `SigilBuild.Updates.Publisher`, `SigilBuild.UpdateSdk` | MIT — `LICENSE` file in repo root |
| `SigilBuild.Cloud.Api` | Proprietary — lives in private `Sigil-build/cloud` repo, never co-located with OSS components |

The MIT/proprietary split is the foundational Open Core boundary (D-002, ADR-005). Code from `SigilBuild.Cloud.Api` must never appear in this repository.

---

## Social / Trademark

| Item | Status |
|---|---|
| Twitter / X `@sigilbuild` | ☐ To register (D-007) |
| Trademark search USPTO/EUIPO Class 9/42 "Sigil" | ☐ To complete before public launch (D-007) |

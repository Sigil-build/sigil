# G2/G3 runbook — what only a human can do from here

Everything in this document needs a human hand: merging (lane PRs never merge
themselves — see `03-RC_ORCHESTRATION.md`'s Failure handling), GitHub repo
settings, secret provisioning, and dispatching a workflow that has never run.
Nothing here can be automated away by another agent pass.

## 1. Merge chain (strict checks force this to be serial)

Order: **S4 [#28](https://github.com/Sigil-build/sigil/pull/28) →
S5 [#29](https://github.com/Sigil-build/sigil/pull/29) →
S6 [#30](https://github.com/Sigil-build/sigil/pull/30) →
S7 [#31](https://github.com/Sigil-build/sigil/pull/31) →
REL [#32](https://github.com/Sigil-build/sigil/pull/32) →
SUP [#33](https://github.com/Sigil-build/sigil/pull/33) →
DOC [#34](https://github.com/Sigil-build/sigil/pull/34) →
runbook [#__](#) (this PR — filled in below once it exists)**.

For each link: approve+merge → the remaining PRs' checks are invalidated →
the orchestrator rebases the next branch onto the new RC head and pushes →
wait for green → merge.

**Pre-flight verification (2026-09-08, local).** All seven lanes were merged
onto a throwaway branch off `release/v0.1.0-alpha` @ `b62de86`, in this exact
order, with **zero git conflicts** at every step (three merges needed git's
ordinary non-overlapping-hunk auto-merge — `ManifestParser.cs` at S6,
`ci.yml` at SUP, `docs/guides/parameters.md` at DOC — none were true
conflicts requiring a manual resolution). The integrated tree, once the
lock-file fix below was applied, passed:

- `dotnet restore Sigil.slnx --locked-mode` — clean
- `dotnet build Sigil.slnx -c Release` — 0 warnings, 0 errors
- `dotnet format Sigil.slnx --verify-no-changes` — clean
- `dotnet test Sigil.slnx -c Release` — **1694 total / 1674 passed / 0 failed / 20 skipped**

This was a **local pre-flight only** — no VM legs, and this machine cannot
Native-AOT-publish (no MSVC C++ workload), so the `aot publish (win-x64)`
CI check on each of these seven PRs is its **first real contact** with the
AOT toolchain. Treat these numbers as "the chain is structurally sound," not
as a substitute for green CI on each PR.

Two known rebase traps:

**Trap 1 — S7 rides S5.** PR #31 (S7) is stacked on S5's branch. After S5
(#29) merges, rebasing S7 onto the new RC head makes its **first 11 commits
vanish into the base** (they are already in history via S4 + S5) — this is
expected, not a problem. Exactly **3 commits remain**: S7's own
`feat(engine): resolve replay anchoring from the signed blob` (R44, R51),
its pinning test, and its adaptation to S5's uninstall-test shape. If the
rebase leaves a different count, stop and diff before force-pushing.

**Trap 2 — SUP after REL: the SkiaSharp lock-file trap.** REL (#32) commits
`packages.lock.json` for all 21 restore-locked projects, generated against
`SkiaSharp` / `SkiaSharp.NativeAssets.Win32` / `SkiaSharp.NativeAssets.Linux`
pinned to the **preview** version `3.119.4-preview.1.1`. SUP (#33) bumps
`Directory.Packages.props` to the **stable** `3.119.4`. Git sees **no
conflict** — SUP's diff never touches a lock file — so the merge itself is
silent. The failure surfaces only at restore: `dotnet restore Sigil.slnx
--locked-mode` against the merged tree fails with **12 `NU1004` errors
across 8 projects** (`SigilBuild.Cli`, `SigilBuild.Installer.Host`,
`SigilBuild.Packaging`, `SigilBuild.Signing`, and the paired
`*.Tests`/`*.IntegrationTests` projects for the first four), all of the
shape:

```
error NU1004: Mismatch between the requestedVersion of a lock file dependency
marked as CentralTransitive and the version specified in the central package
management file. Lock file version [3.119.4-preview.1.1, ), central package
management version [3.119.4, ).
```

Verified for real in the local pre-flight, not theoretical. **Fix, during
SUP's rebase:** run `dotnet restore Sigil.slnx --force-evaluate`
(regenerates 11 of the 21 `packages.lock.json` files) and commit the
regenerated files **as part of the rebase commit**, before pushing. Confirm
with a clean, non-piped `dotnet restore Sigil.slnx --locked-mode` before
calling SUP green — a piped run (`| tail`, etc.) reports the pipe's exit
code, not restore's, and will falsely read as success.

## 2. Repo settings (owner-only, before G2 closes)

- **Settings → Security → Private vulnerability reporting → enable.**
  Verified **OFF** on 2026-09-08 via
  `gh api repos/Sigil-build/sigil/private-vulnerability-reporting` →
  `{"enabled":false}`. Required for R23 / G2. This is a repo-owner action —
  no automation in this preparation can flip it, and it should not be
  automated even if a token with the right scope existed.
- **Provision Azure Trusted Signing and add six Actions secrets.**
  `gh secret list` returns **zero** secrets on this repo today — confirmed
  empty, not merely unlisted:
  `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`,
  `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`,
  `TRUSTED_SIGNING_PROFILE`. `release.yml` (landing with REL #32) refuses to
  run without them — by design, not a bug to route around. **Start this
  early** — Trusted Signing account/certificate-profile provisioning has
  real lead time, and G3's dry-run tag (Section 4) cannot exercise signing
  without it.
- **Reserve NuGet package IDs `SigilBuild` and `SigilBuild.UpdateSdk`**
  (R41a, a G4 item — do it now anyway, before the repo gets public
  attention and someone else claims either name).

## 3. G2 manual checks (after DOC merges) — commands inline

Source: the ten-item "G2 — after Stages 2 and 3" checklist in
`docs/plan/release/03-RC_ORCHESTRATION.md`. All ten need the **merged RC**
(after DOC #34) — several exercise behavior that lands only with S4, REL, or
DOC. Run from a **clean clone** of `release/v0.1.0-alpha` taken after DOC
merges, not from a stale worktree.

1. **Silent-install line succeeds (R26).** Copy the exact silent-install
   example out of the **post-DOC-merge** `docs/guides/parameters.md` — R26's
   fix replaces the old `/install_dir=…` / `/edition=…` form (which the
   parser rejects with `UsageException: unrecognized flag`) with the form
   the parser actually accepts, `/P<Name>=<Value>`, e.g.
   `Setup.exe /S /Pinstall_dir="C:\Apps\MyApp" /Pedition=professional` — and
   run it against a real `Setup.exe`. Expected: exits `0` (or `3010` if a
   reboot is pending), installs silently to the given directory, no
   `UsageException`. **This box cannot build `Setup.exe`** (no AOT
   toolchain) — needs the CI-built artifact (`ci.yml`'s
   `publish/win-x64/*` upload, which Task 2 fixed to include the sibling
   native DLLs) or a signed release artifact. This box cannot produce that
   artifact itself.

2. **`dotnet restore --locked-mode` succeeds from a clean clone (R23a).**
   From a fresh `git clone` (not a reused worktree) of `release/v0.1.0-alpha`
   at the post-DOC-merge head: `dotnet restore Sigil.slnx --locked-mode`.
   Expected: exit `0`, all 21 projects restore against their committed lock
   files, no `NU1004`. This is Trap 2's real proof — if SUP's rebase folded
   the regenerated lock files correctly, this passes; if not, it fails
   exactly as the pre-flight did before the fix.

3. **`sigil init --template full-config` produces a manifest that packs
   (R30).**
   ```
   sigil init --template full-config --out sigil.yaml
   sigil pack sigil.yaml --out ./dist
   ```
   Expected: `pack` exits `0`. Confirm the generated manifest's
   `updates.signingKey` is R30's corrected base64 SPKI **placeholder**, not
   a file path — and that pack-time validation (also part of R30's fix)
   accepts it as a decodable, importable P-256 SPKI key.

4. **A manifest with `source: { url: "http://…" }` fails to pack (R8).**
   Pack a manifest whose `parameters.<name>.source.url` (the dynamic-dropdown
   source under `parameters`, `schemas/sigil-schema.json` near lines 45 and
   624) is `http://example.invalid/options.json` instead of `https://`.
   Expected: pack fails with a pack-time diagnostic mirroring the existing
   `SIG0235` (`http_download`'s HTTPS-only check) — S4 adds this check; its
   exact new `SIG02xx` code is only knowable once #28 is read post-merge.
   Also confirm the same rejection re-fires at install time if
   `Installer.Host/Services/HttpOptionsLoader.cs` re-checks the URL before
   fetching, per R8's stated fix.

5. **A manifest with `updates: { manifestUrl: "http://…" }` fails to pack
   (R14).** Pack a manifest with
   `updates: { manifestUrl: "http://example.invalid/channel.json" }`.
   Expected: pack fails. Today `schemas/sigil-schema.json`'s `manifestUrl`
   only enforces `"format": "uri"` (which accepts `http://`); S4 adds an
   explicit HTTPS-only pack-time check per R14's fix. Also confirm the same
   rejection at fetch time in `Update/UpdateSeams.cs`.

6. **A replayed stale signed channel manifest is rejected (R13).** Host a
   channel manifest signed with a test P-256 key, install the app, then
   serve a **replay** — either an `issuedAt` older than the freshness window
   or a `sequence` lower than one already seen for this install — and run
   `Setup.exe /Update`. Expected: rejected as a hard security failure, not
   accepted as "up to date." Confirm the exact exit code against the
   **DOC-merged** `docs/guides/updates.md` exit-code table — today's table
   (pre-S4) reserves exit `8` specifically for "signature rejected"; check
   whether R13's freshness rejection reuses `8` or gets its own code rather
   than assuming. The property that actually matters: the client must not
   report exit `0` ("up to date") for a manifest it has already seen at a
   higher sequence, and must not install an intermediate vulnerable version
   via a replayed manifest.

7. **`THIRD-PARTY-NOTICES.md` names Skia, ANGLE, HarfBuzz, and libsodium
   explicitly (R23).**
   `grep -E "Skia|ANGLE|HarfBuzz|libsodium" THIRD-PARTY-NOTICES.md`
   (file lands with REL #32 — confirmed **absent** from `release/v0.1.0-alpha`
   today). Expected: all four names present.

8. **`SECURITY.md` exists and GitHub private vulnerability reporting is on
   (R23).** `test -f SECURITY.md` (file lands with REL #32) and re-run
   `gh api repos/Sigil-build/sigil/private-vulnerability-reporting`.
   Expected: file present; API returns `{"enabled":true}` — the second half
   depends on Section 2's manual settings flip, not on any PR merging.

9. **No stray version literal (R24).**
   `grep -rn "0\.0\.1-alpha" --include='*.cs' --include='*.csproj' --include='*.yml' .`
   Expected: **empty**. Verified **today** (pre-REL) this still finds hits —
   `src/SigilBuild.Cli/SigilBuild.Cli.csproj:9`,
   `src/SigilBuild.Cli/Program.cs:11`,
   `tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36`,
   `.github/workflows/ci.yml:136` — plus stale `obj/` build-artifact hits on
   a locally-built tree (exclude `obj/`/`bin/`, or expect noise on anything
   but a fresh clone). REL (#32) unifies these to one source of truth; this
   box should clear once REL (and DOC, which may also reference the
   version) has merged.

10. **The vulnerability scan ran; its findings are recorded as fixed or
    accepted (R42).** SUP (#33) adds Dependabot (nuget + github-actions,
    weekly) and a `dotnet list package --vulnerable --include-transitive`
    CI step. After SUP merges, confirm with `gh run list --workflow=ci.yml`
    that the step actually ran (not just that CI passed — the step could be
    silently absent from a given run), then read its output. Expected: any
    High/Critical findings are either fixed or have a recorded, explicit
    acceptance — not silently green because nothing was scanned.

## 4. G3 (Stage 4 / V1) prerequisites

- **Dispatch `wrapper-vm-tests.yml` once by hand.** Confirmed via
  `gh workflow list --all` that it is `active` but **`workflow_dispatch`-only
  and has never run** — zero runs in its history. Run it for real
  (`gh workflow run wrapper-vm-tests.yml`) against non-vacuous tests, then
  add a schedule trigger (or a merge trigger) via a follow-up PR so it stops
  being on-demand-only, per the G3 checkbox.
- **`release.yml` will not appear in `gh workflow list` until it first
  fires.** Confirmed empirically: `gh workflow list --all` today shows only
  `ci`, `docs`, `pr-guards`, `secret-scan`, `wrapper-vm-tests` — no
  `release`, even though `.github/workflows/release.yml` already exists on
  `rc/rel-scaffolding` (landing on `release/v0.1.0-alpha` via #32). This is
  expected GitHub Actions platform behavior, not a defect: `release.yml`
  triggers **only** on `push: tags: ['v*']`, and GitHub registers a workflow
  in the listing only once it has run at least once, or lives on the
  **default branch** (`main`, per `gh repo view`) — neither is true yet. It
  registers for real at **V1's dry-run tag**, which is GitHub's first
  authoritative parse of the YAML. Do not treat "not listed" as a reason to
  hold #32.
- **`release.yml`'s six required secrets** (Section 2) are enforced by
  design — the workflow is written to refuse to run without them, not merely
  to fail partway through a run.
- **`azure/trusted-signing-action@v2.0.0` pin.** REL (#32) pins the
  major-version-2 release (`v0.5.1` was no longer the newest tag when
  checked via `gh api repos/azure/trusted-signing-action/tags`). It carries
  one deprecated-but-functional input name,
  `trusted-signing-account-name` — expect a cosmetic deprecation warning in
  the dry-run's log, not a failure.
- **V1 lane runs per `08-STAGE-4-verification.md`**: walk the 60-row
  register, re-attack, re-measure (including R48's worst-case cold-cache
  number — a human-only measurement documented in that file), release
  dry-run with a throwaway prerelease tag (exercises `release.yml` end to
  end, including real Trusted Signing), then a **clean-machine install of
  the downloaded artifact** — verified by actually running it, not by
  reading the workflow log (R7's sibling-DLL trap is exactly the kind of
  failure a log-only check would miss).

## 5. What this preparation did NOT verify

- **No AOT publish ran on the dev machine** (missing MSVC C++ workload, a
  known local blocker). Every one of the seven Stage 2/3 PRs'
  `aot publish (win-x64)` CI checks is that lane's **first real contact**
  with the Native AOT toolchain — the pre-flight's green build/test/format
  numbers say nothing about AOT trim safety.
- **`release.yml` has never executed.** Its first run of any kind is V1's
  dry-run tag at G3. The YAML has been read carefully (Task 2) but never
  parsed by GitHub Actions, and never run against real Trusted Signing
  credentials.
- **The integrated-tree numbers cited in the seven PR bodies and in Section 1
  above are local-only.** No VM legs, no elevation test, no cross-account
  UAC decrypt — see Section 6(b) below for the specific untested claim that
  most needs one.
- **`wrapper-vm-tests.yml` has never run**, on any branch, ever. Its entire
  test surface is unverified in CI — only in local unit tests, where
  DPAPI/elevation allow it.

## 6. Register-row candidates found during this preparation (orchestrator files at gate close)

None of these block G2. All four were found as a side effect of Task 3's R18
work (secrets off the elevated relaunch command line, landing in S5 #29) and
are explicitly out of that task's scope. File them in `00-GAP_REGISTER.md`
at the next gate-close docs pass.

(a) **Redaction gap in the always-on wizard log.**
`src/SigilBuild.Installer.Host/Program.cs:190` logs
`wizard started: pid=…, argv=[{string.Join(' ', args)}], cwd=…` —
**raw, unredacted argv** — to the log that is on by default. After R18, the
*elevated child's* argv is safe (it carries only `/SecretHandoff=<path>`),
so this line strictly improved; but a per-user, non-elevating install
invoked directly with `/P<secret>=<value>` still writes the plaintext
secret value into that log file. A different channel from R18's
process-auditing scope — found during R18 review, deliberately not folded
into that commit. Fix is one call to `session.CommandLine.AuditSafeRendering()`.

(b) **`wrapper-vm-tests.yml` candidate case: cross-account UAC decrypt.**
R18's DPAPI envelope uses `CRYPTPROTECT_LOCAL_MACHINE`, documented to let an
elevated child — potentially a *different* administrator account than the
one that launched setup — decrypt a machine-scope blob the unelevated
parent wrote. Every existing unit test drives the envelope in-process;
nothing exercises a real `ShellExecuteExW` + `runas` relaunch across
accounts. This rests on documented DPAPI semantics only, with no test.
Candidate case for the VM matrix once it grows an elevation leg.

(c) **Enhancement: pack-time diagnostic for a secret interpolated into
`run_program.args`.** `docs/guides/parameters.md`'s R18-added Secrets
section documents — but does not enforce — that a secret parameter
reference inside a `run_program` step's `args` still leaks to that child
process's command line (a different channel from the elevated-relaunch one
R18 closes; today it is a documented non-guarantee, not a bug). The natural
next step, if the track wants teeth rather than a documented limitation, is
a new `SIG02xx` diagnostic (install_steps band) firing at pack time when a
secret-typed parameter reference appears inside `run_program.args`. A
schema/diagnostics change with its own lockstep surfaces per `AGENTS.md` —
belongs in its own task.

(d) **Hardening follow-ups from the R18 review.** Two defense-in-depth items
noted but not required for R18's fail-closed correctness: a secondary DPAPI
entropy value (on top of the DACL, delete-on-read, size cap, and buffer
zeroing already implemented), and a reaper for stale
`sigil-elevate-*.dpapi` files that could accumulate in `%TEMP%` from a
parent that crashed or was killed before reaching its cleanup `finally`.

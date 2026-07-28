# Release readiness — Sigil, first public release

Audit date **2026-07-28** · tree `main` @ `1be494c` · audited on Windows 11,
.NET SDK 10.0.302.

Companions: [`00-GAP_REGISTER.md`](00-GAP_REGISTER.md) (every finding, with
evidence) · [`01-FIX_PLAN.md`](01-FIX_PLAN.md) (who fixes what, when).

---

## Verdict

**Not releasable today.** The engineering is genuinely strong — the build is
warning-clean under Native AOT, 1,096 tests pass, the update-manifest signature
scheme is correct in the way that matters most, and the code shows real
discipline about argument construction, traversal containment, and secret
redaction. But the installer has **five distinct local privilege-escalation
paths**, four of which are exploitable against a *correctly authored* manifest,
and the test suite that would have caught them reports green while proving
almost nothing: roughly two dozen "passing" tests — every real install,
uninstall, scope, upgrade, and `Setup.exe`-stamping proof — are early returns
that assert nothing, and the workflow that runs them for real is manual-dispatch
only. Shipping this as-is means publishing privileged software with a
one-JSON-file path from unprivileged user to SYSTEM. **The shortest honest path
is about two weeks**: one mechanical day to clear a formatting backlog and
install the CI gates that were written but never turned on, then four parallel
security lanes (~1 week), then scaffolding and docs (~3–4 days), then a
verification pass that runs the VM matrix for the first time against
non-vacuous tests. None of the blockers is architectural; they are all missing
checks in otherwise sound code.

---

## What actually works

This section is not padding. It is the reason the two-week estimate is two
weeks and not two months.

- **The update signature scheme is right.** This was the highest-stakes thing
  in the P-track and it holds up under adversarial reading. The signature
  covers the exact fetched bytes, and **every** field the client consumes lives
  inside that range — no canonical subset, no unsigned siblings
  (`Update/UpdateRunner.cs:102,105,116`). The public key is pinned into the
  stamped blob at pack time with no runtime override path. Verification fails
  closed on a missing key, missing signature, malformed base64, or a non-P-256
  curve, and precedes every download. Its test suite covers tampered bytes,
  wrong key, wrong curve, and DER-vs-P1363 encoding confusion.
- **SHA-256 is mandatory and unskippable on every path that executes a
  downloaded artifact** — enforced at pack time *and* re-checked at run time,
  with no code path reaching a process launch on an unverified file.
- **No shell, anywhere.** Every external tool (`schtasks`, `netsh`, `sc`,
  `run_program`) launches with an explicit filename and per-argument
  `ArgumentList`. No `cmd.exe`, no `UseShellExecute` in the install path. **No
  scheduled-task XML is constructed at all**, so the forged-`Principal`
  injection class simply does not exist here. Privileged enum fields are closed
  sets validated twice.
- **Wizard localization is inert.** Manifest-supplied translations reach
  `TextBlock.Text` and nothing else — no `string.Format` anywhere in `src/`, no
  runtime XAML, no shell. An explicit audit question with a clean answer.
- **Traversal containment is correct everywhere it exists** — `payload://`
  resolution, zip-slip, and the native-runtime archive all normalize before
  comparing and terminate the root prefix properly.
- **Secret redaction reaches the log, the journal, and the persisted state**
  before bytes touch disk.
- **The security-critical unit tests are good** — real negative cases, real TLS
  servers, real tampering. The problem is not test quality; it is that the
  *integration* tests are vacuous.
- **Build hygiene:** zero warnings under `TreatWarningsAsErrors` with the AOT
  analyzer on, zero `TODO`/`FIXME`/`HACK` in `src/`, no build output or
  coverage report committed, a 1.99 MiB repo.

---

## The blockers, one line each

| # | Blocker |
|---|---|
| **R1** | Elevated install *and* uninstall load and replay a JSON journal any unprivileged user can write; one `unregister_com` record is arbitrary code as admin. |
| **R2** | Elevated install spawns an executable path read from **HKCU** during upgrade detection — no signature check, no path validation. |
| **R3** | `/D=` sets `install_dir` anywhere with no containment, and privileged steps resolve their targets from it — a **SYSTEM scheduled task pointed at a user-writable directory**, reachable from the documented example manifest. |
| **R4** | The elevated process extracts and `AddDllDirectory`s a **per-user** native cache guarded only by a marker file — plant a DLL, own the installer. |
| **R5** | The web-installer stub downloads to a **predictable** `%TEMP%` filename, verifies it, then executes it elevated in a separate step. |
| **R6** | ~24 integration tests soft-skip by returning early, so they report **Passed**; every real install/uninstall/scope/upgrade proof is vacuous, and the workflow that runs them properly is `workflow_dispatch` only. |
| **R7** | No release workflow, no tag, no signed artifact — and the one CI artifact uploads `sigil.exe` without the two native DLLs it needs to run. README advertises three install channels that do not exist. |

R1 and R2 were found independently by three and two audit lanes respectively.
R3 I derived and verified directly. Full evidence, with file:line, in the
register.

---

## Measured numbers

| Check | Result |
|---|---|
| `dotnet build Sigil.slnx -c Release` | **0 warnings, 0 errors** (41.5 s) |
| `dotnet test Sigil.slnx -c Release` | **1097 total — 1096 passed, 1 skipped, 0 failed** |
| Honest skips in that run | **1** (`ComRegisterInstallTests`, legitimately justified) |
| Vacuous passes hidden in that run | **~24** (14 VM-gated + ~10 runtime-gated) |
| `dotnet format --verify-no-changes` | **FAILS — exit 2, 28 of 465 files** |
| Coverage, project-wide union | **75.17 %** (gate: 65 %) — *local reports, not a CI run* |
| Coverage, `SigilBuild.Core` | **63.89 %** — below its 80 % target *and* below the 65 % gate |
| Coverage, `SigilBuild.Signing` | **68.79 %** — target 85 % |
| Assemblies missing from the coverage denominator | **3** — `Cli`, `Wrapper`, `Installer.Host` |
| `sigil.exe` (stale local publish, 2026-07-24) | 13.98 MB vs the 15 MB gate — ~7 % headroom |

For contrast, `ORCHESTRATION_PLAN.md:6` still claims "527 tests green". The
plan docs have been outgrown by the tree; treat this register as the correction.

---

## What I could not verify

Stated plainly, because a readiness report that hides its gaps is the same
failure mode as R6.

- **Native AOT publish does not work on this machine.**
  `dotnet publish -p:PublishAot=true` fails at the link step — `vswhere.exe` is
  not resolvable and the MSVC linker returns `MSB3073` / exit 123. Therefore
  **the actual release artifact was never built or run during this audit**, and
  the size gates (15 MB CLI, 45 MB host) are unverified here. CI on
  `windows-latest` is the only evidence for those.
- **The VM matrix was not run.** `wrapper-vm-tests.yml` needs admin rights and
  a disposable machine; running it here risked the dev box. So no real install,
  uninstall, scope, upgrade, or system-step behavior was observed by this audit
  — only read. Given R6, it has also never been observed by *any* per-push CI
  run.
- **Coverage percentages come from a local, untracked `TestResults` tree dated
  2026-07-24**, computed by re-running the CI gate's exact algorithm. That tree
  may hold multiple report generations, which the max-hits union would slightly
  inflate. A CI run log is the authoritative source.
- **Dependency advisory status.** I did not run
  `dotnet list package --vulnerable`. Every version is flagged UNVERIFIED
  rather than guessed; the fix plan adds the scan to CI rather than relying on
  judgement.
- **R31** (whether a leading-quote `program` value can shift which token Task
  Scheduler treats as the executable) is marked UNVERIFIED in the register.
- **Whether the GitHub repo is already public.** If it is, the "don't announce
  yet" framing below matters more than the "don't publish yet" framing.

---

## What ships unproven, and what that risks

Per-push CI proves: it compiles clean in Release (so the AOT/trim analyzers
pass), `sigil.exe` AOT-links under 15 MB and can `--version`/`init`/`validate`
every example manifest, the installer host AOT-links under 45 MB, unit-level
logic across the manifest graph, expression engine, blob serialization, update
signature verification, payload codec, and step argument construction, plus
docs-drift and secret scanning.

Per-push CI proves **nothing** about: a packed `Setup.exe` actually installing
anything; uninstall reversing it; per-user vs per-machine scope correctness
(ARP hive, install roots, PATH, shortcuts); version-aware upgrade, blocked
downgrade, or `/force-downgrade`; double-install idempotency; uninstall after
the original setup exe is deleted; prerequisite detect→install→3010; files-in-use
and the setup mutex; the live `schtasks`/`netsh`/COM legs; the update flow
end to end; or the `--payload web` stub. All of that lives only in a
manually-dispatched workflow.

**The risk in one sentence:** the first person to actually exercise the product
end to end would be a member of the public, on their own machine, with admin
rights, and the failure would be discovered in the wild rather than in CI.

---

## Known limitations — draft for release notes

Lift this into the release once the blockers are fixed; it is written to be
honest rather than flattering.

> **Sigil 0.1.0-alpha is Windows-only and pre-production.** It builds and
> installs real software, but it has not yet been run at scale outside its own
> test suite. Do not use it to ship an installer to end users you cannot reach
> with a correction.
>
> - **Windows only.** The pack host must be Windows (`BeginUpdateResourceW` has
>   no cross-platform equivalent), and the produced installers are Windows-only.
>   There is no macOS or Linux story, now or planned for v1.
> - **Delta updates are not implemented.** `/Update` performs full-package
>   updates. The zstd-dictionary delta format and the client SDK described in
>   earlier material are deferred — see ADR-010.
> - **Update manifests are authenticated but not yet freshness-checked.** A
>   signed manifest is verified against a pinned P-256 key, but an attacker who
>   can serve stale content may be able to suppress an update. Serve update
>   manifests over HTTPS from infrastructure you control.
> - **Machine-scope installs require care with `install_dir`.** Installing to a
>   directory writable by non-administrators is refused; do not work around it.
> - **`com_register` runs the publisher's `DllRegisterServer` inside the
>   elevated installer process.** A faulty DLL takes the installer with it.
> - **Prerequisite and update payloads are verified by SHA-256 and Authenticode
>   before execution**, but Sigil cannot vouch for what a third-party
>   redistributable does once it runs.
> - **Preview dependencies.** The rendering stack pins a preview SkiaSharp
>   build to satisfy Avalonia 12, and the CLI uses a System.CommandLine beta.
> - **Coverage:** ~75 % project-wide. `SigilBuild.Core` and `SigilBuild.Signing`
>   sit below their targets and are the areas most likely to hold undiscovered
>   bugs.
> - **Report security issues privately** via SECURITY.md. Please do not open a
>   public issue for a privilege-escalation finding.

---

## Recommended release shape

**Cut `v0.1.0-alpha` after the fix plan completes — and do not announce it.**

Reasoning against the alternatives:

- **`0.1.0` (no pre-release tag) — no.** Semver's pre-release marker is the
  cheapest possible way to set expectations, and this software has never been
  run by anyone outside the repo. A bare `0.1.0` from an unknown publisher, for
  a tool that elevates to admin, claims a confidence nobody has earned yet. It
  also makes the first breaking change awkward, and there will be breaking
  changes: the manifest schema is going to move once real users push on it.
- **Private tag, no public release — no, but nearly.** This would be right if
  the repo were still private. The remote, the CI badges, and the org already
  exist, so the marginal secrecy is small, and a public alpha is how you get the
  external testing that R6 shows the project cannot generate internally. The
  half of this option worth keeping is the **discipline of not announcing**.
- **`v0.1.0-alpha`, published, unannounced — yes.** Tag it, attach signed
  checksummed artifacts and the notices, write the honest limitations section
  above, and let it sit. Point early adopters at it individually. Hold the
  launch post until a `v0.2.0` that has (a) at least one external user's
  successful install, (b) a green VM matrix on a schedule rather than on
  demand, and (c) `SigilBuild.Core` coverage back above its 80 % bar.

One sequencing note: **reserve the `SigilBuild` and `SigilBuild.UpdateSdk` NuGet
IDs before the repo gets any attention.** The README already advertises
`dotnet tool install -g SigilBuild` against an ID that
`docs/sprint-01/identifier-reservation.md:13` says was never actually published.
That is a free name-squat waiting to happen.

---

## Definition of done for v1

Tick every box before tagging. V1 (the verification lane) owns this list.

**Security — no box here is optional**

- [ ] **R1** A planted `uninstall.json` in `%ProgramData%` or `%LocalAppData%` is refused by an elevated install *and* an elevated uninstall, with a log line saying why — verified by hand as a standard user, not only by test.
- [ ] **R1** Journal replay is anchored: out-of-`install_dir` paths and out-of-subtree registry coordinates are rejected.
- [ ] **R1** The machine state directory is created with an explicit admin-only DACL, and load is gated on an ownership check.
- [ ] **R2** A machine-scope resolve ignores HKCU entirely; a prior uninstaller is Authenticode-verified or admin-path-constrained before it is spawned.
- [ ] **R3** `/D=` outside the scope root is rejected; privileged step targets are contained and non-user-writable.
- [ ] **R4** A pre-planted native-runtime cache with a valid marker is not trusted by an elevated run.
- [ ] **R5 / R12** Every downloaded binary is staged in an admin-only randomly named directory and re-verified immediately before launch.
- [ ] **R11** Authenticode verification gates every downloaded binary's execution.
- [ ] **R13** Channel manifests carry signed freshness data and replays are rejected; the ADR is written.
- [ ] **R8 / R14** No manifest field can carry a cleartext `http://` URL into an elevated install.
- [ ] Each of the above has a negative test **confirmed to fail on the parent commit**.

**Proof**

- [ ] **R6** `dotnet test` reports a non-zero skip count, and the number is recorded here.
- [ ] **R6** No test soft-skips by returning early; every skip is an `Assert.Skip` with an actionable reason.
- [ ] **R6** The `build` job stages the AOT runtime, so the pack→`Setup.exe` path executes on every push.
- [ ] **R22** All three VM jobs fail loudly rather than passing vacuously when their preconditions are absent.
- [ ] **R21** Per-assembly coverage floors are enforced and no shipping assembly is missing from the denominator.
- [ ] `wrapper-vm-tests.yml` has been run against non-vacuous tests, is green, and its run URL is recorded here.
- [ ] The VM matrix runs on a schedule or on merge to `main`, not only on demand.

**Release mechanics**

- [ ] **R7** `release.yml` produces signed, checksummed artifacts on a `v*` tag, gated on the VM matrix.
- [ ] **R7** The published artifact **runs on a clean machine** — verified by downloading it, not by inspecting the workflow (the sibling-DLL trap).
- [ ] **R24** One version literal in the repo; `--version` agrees with it.
- [ ] **R23** `SECURITY.md`, `CHANGELOG.md`, and `THIRD-PARTY-NOTICES.md` exist; the notices name Skia, ANGLE, HarfBuzz, and libsodium explicitly.
- [ ] **R23a** `dotnet restore --locked-mode` succeeds from a clean clone.
- [ ] **R42** A vulnerability scan runs in CI, and its current findings are recorded (fixed or accepted).
- [ ] NuGet IDs reserved.

**Truth in documentation**

- [ ] **R26** The documented silent-install command has been copy-pasted and observed to succeed.
- [ ] **R26** `docs/setup-exe-reference.md` covers all fifteen runtime tokens.
- [ ] **R25** README describes the product that exists and promises nothing that does not.
- [ ] **R20** `dotnet format --verify-no-changes` is clean *and* enforced in CI; `pr-guards` has been watched failing a bad PR title.
- [ ] Every remaining register row is either demonstrated fixed or listed in the release notes' known limitations.

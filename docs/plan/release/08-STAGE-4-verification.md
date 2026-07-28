# Stage 4 — Verification and release

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:executing-plans`.
> Steps use checkbox (`- [ ]`) syntax. Global constraints live in
> [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints).

**Goal:** Decide go/no-go on evidence, then tag.

**Architecture:** One lane, run alone, after every other lane has merged into
the RC. **You are the adversary, not the author.** Assume each lane's fix is
incomplete until you have tried to make it fail.

**Runs after:** Gate G2. **Produces:** Gates G3 and G4.

| Lane | Model | Branch | Findings |
|---|---|---|---|
| `V1` verification | opus-5 | `rc/v1-verification` | re-verifies all 46 |

**Out of scope: fixing anything you find.** File it as a new row in
`00-GAP_REGISTER.md` and reopen the owning lane. Fixing forward in the RC
destroys the property that makes this stage meaningful — that the thing you
verified is the thing that ships.

---

## Task V1.1: Walk the register

**Files:** Modify `00-GAP_REGISTER.md` (status per row), `02-READINESS_REPORT.md`
(Definition of Done)

- [ ] **Step 1:** For each of the 46 rows, record **either** the test name or
      manual step that demonstrates the fix and its observed output, **or** an
      explicit deferral with a one-line justification. **No row may be silently
      dropped** — that is the single failure mode this task exists to prevent.
- [ ] **Step 2:** Cross-check against the lane→row map in
      `03-RC_ORCHESTRATION.md`. Every row belongs to exactly one lane; if a lane
      merged without touching one of its rows, that is a finding.
- [ ] **Step 3:** Verify the negative-test discipline actually held. For a
      **sample of at least six** security fixes across S1/S2/S3/S4, check out the
      commit's parent and confirm the test fails there:

```bash
git log --oneline release/v0.1.0-alpha | grep "fix(security)"
git checkout <sha>~1 -- src/
dotnet test -c Release --filter "FullyQualifiedName~<TheTest>"   # expect FAIL
git checkout <sha> -- src/
```

  A lane that shipped a test passing on both sides has not fixed anything, and
  its row goes back to "open".

## Task V1.2: Re-attack, by hand, as a standard user

Do not delegate this to the test suite. The suite tests what the authors thought
of; this step is where you think of something else.

- [ ] **Step 1 — R1, planted state:** plant `uninstall.json` in
      `C:\ProgramData\Sigil\<AppId>\` **and** in `%LocalAppData%\Sigil\<AppId>\`
      with a `restore_file` record targeting `C:\Windows\System32\`, an
      `unregister_com` record naming a DLL you control, and a
      `restore_registry_value` under `HKLM\SYSTEM\CurrentControlSet\Services\`.
      Run an elevated machine-scope **install** and an elevated **uninstall**.
      Record what the log said for each record.
- [ ] **Step 2 — R2, planted ARP:** plant
      `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\<AppId>` with a
      low `DisplayVersion` and an `UninstallString` pointing at a scratch exe
      that writes a marker file. Run an elevated machine-scope install. **Assert
      the marker file does not exist.**
- [ ] **Step 3 — R3, hostile `/D=`:** run
      `Setup.exe /allusers /D=C:\Users\Public\evil`, and also try a junction:
      create `C:\Program Files\App` as a junction to a user-writable directory
      and install into it.
- [ ] **Step 4 — R4, planted native cache:** pre-create
      `%LocalAppData%\Sigil\runtime\<sha>\` with a bogus `libSkiaSharp.dll` and
      a valid `.sigil-runtime-complete` marker, then launch the elevated wizard.
- [ ] **Step 5 — R5/R12, staging race:** run the web-stub install under a loop
      that repeatedly rewrites the staged package between download and launch.
      You are trying to lose — if you win the race, the fix is incomplete.
- [ ] **Step 6:** Write up each attempt with its observed outcome. "Refused" is
      not a result; the log line is.

## Task V1.3: Run the VM matrix for real

- [ ] **Step 1:** Dispatch `wrapper-vm-tests.yml`. **Record the run URL.**
- [ ] **Step 2: Read the log, do not just read the badge.** This is the first
      time in the project's history that this matrix runs against
      **non-vacuous** tests — before T1, every one of these soft-skipped to a
      pass. Confirm the previously-skipping tests **executed**: look for the
      upgrade, downgrade-blocked, scope-matrix, files-in-use, and system-step
      legs actually asserting.
- [ ] **Step 3:** Confirm T1's pre-flight guards work by checking that the jobs
      would have failed with `SIGIL_VM_TESTS` unset (T1 tested this; re-confirm
      the guard survived the merges).
- [ ] **Step 4:** Move the matrix to a schedule or on-merge trigger. A proof
      that only runs when someone remembers to click is not a proof — it is the
      condition that produced R6.
- [ ] **Step 5:** Record which legs ran, which skipped, and why.

## Task V1.4: Re-measure everything

- [ ] **Step 1: Local**

```bash
dotnet build Sigil.slnx -c Release 2>&1 | tail -5
dotnet test Sigil.slnx -c Release 2>&1 | grep -E "^(Passed|Failed)!"
dotnet format Sigil.slnx --verify-no-changes; echo "format exit=$?"
```

Record: warnings, total/passed/**skipped**/failed, format exit.

The skip count is the headline number of this whole track. Before Stage 1 it
was **1** against 1096 passes, roughly 24 of which asserted nothing. State the
new figure plainly.

- [ ] **Step 2: From CI** — per-assembly coverage against T1's floors, and the
      size gates (`sigil.exe` ≤ 15 MB, host ≤ 45 MB). **AOT publish does not
      work on the dev machine**, so these are CI-only; report them as such.
- [ ] **Step 3: Compare** against the audit baseline in
      `02-READINESS_REPORT.md` and explain any regression.

## Task V1.5: Release dry-run

- [ ] **Step 1:** Tag a throwaway prerelease (e.g. `v0.1.0-alpha.rc1`) and let
      `release.yml` run.
- [ ] **Step 2:** Confirm it produced signed, checksummed artifacts with
      `THIRD-PARTY-NOTICES.md` attached, and that the VM matrix gated it.
- [ ] **Step 3: Download the artifact onto a clean machine and run it.** Not a
      dev box — a machine without the .NET SDK, without MSVC, without the repo.
      Run `sigil --version`, `sigil init`, `sigil validate`, and a real
      `sigil pack --format exe`.

      This is R7's sibling-DLL trap: `ci.yml:224` used to upload `sigil.exe`
      alone while the AOT output needs `libSkiaSharp.dll` and `libsodium.dll`
      beside it. **Reading the workflow does not test this. Running the download
      does.**
- [ ] **Step 4:** Verify the checksums independently and confirm the
      Authenticode signature.
- [ ] **Step 5:** Delete the throwaway tag and its release.

## Task V1.6: The verdict

- [ ] **Step 1:** Update `02-READINESS_REPORT.md` — tick every Definition of
      Done box or mark it explicitly deferred. Replace the "Verdict" paragraph
      with the current one. Replace the measured-numbers table with your figures.
- [ ] **Step 2:** Update the "what I could not verify" section honestly. There
      will still be things — the dev box cannot AOT-publish, and no external user
      has run this. Say so.
- [ ] **Step 3:** Write a one-paragraph **go/no-go recommendation**. If the
      answer is no-go, say exactly what is missing and what it would take. A
      qualified go with named residual risk is a legitimate answer; a
      go-because-we-ran-out-of-time is not.
- [ ] **Step 4:** Update the status line in `03-RC_ORCHESTRATION.md` and the
      progress table.
- [ ] **Step 5: Commit and open the PR for gate G3.**

---

## Gate G4 — release (orchestrator, after G3 passes)

Not lane work. The orchestrator performs these.

- [ ] **Reserve the NuGet IDs first.** `SigilBuild` and `SigilBuild.UpdateSdk`
      are still unclaimed while `README.md` advertises
      `dotnet tool install -g SigilBuild`. Do this **before** the repo gets any
      attention — an unclaimed ID your own README points at is a free squat.
- [ ] Open the RC → `main` PR. It is large by design; the stage documents and
      the register are its review guide.
- [ ] Merge, then tag `v0.1.0-alpha`.
- [ ] Release notes = the known-limitations draft from
      `02-READINESS_REPORT.md`.
- [ ] **Do not announce.** Hold the launch post for a `v0.2.0` with (a) at least
      one external user's successful install, (b) a scheduled green VM matrix,
      and (c) `SigilBuild.Core` coverage back above its 80 % bar. Point early
      adopters at the release individually.
- [ ] Delete the `archive/*` tags.
- [ ] Delete the RC branch.

---

## Lane V1 definition of done

- [ ] All 46 rows demonstrated fixed or explicitly deferred in writing
- [ ] Negative-test discipline spot-checked on ≥ 6 security fixes by checking
      out parent commits
- [ ] Five hand-run attacks attempted and written up with observed log output
- [ ] VM matrix run green against non-vacuous tests, run URL recorded, trigger
      moved off manual-dispatch
- [ ] A downloaded release artifact ran on a clean machine
- [ ] `02-READINESS_REPORT.md` carries a current verdict and honest numbers
- [ ] Nothing was fixed forward — every finding went back to its owning lane

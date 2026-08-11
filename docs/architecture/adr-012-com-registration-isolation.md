# ADR-012: `com_register` loads the publisher's DLL in-process

- **Status:** Accepted
- **Date:** 2026-08-11
- **Decision driver:** Register row **R36** (`docs/plan/release/00-GAP_REGISTER.md`),
  raised during the v0.1.0-alpha release-candidate audit and assigned to
  Stage 2 lane S6. The row observes that `DllRegisterServer` executes
  **in-process at high integrity**, so a malformed or hijacked publisher DLL
  crashes or takes over the elevated installer rather than a disposable child,
  and asks for either an ADR recording the trust assumption or a move to a child
  process. This is that ADR, and it chooses the former.

---

## Decision (TL;DR)

`com_register` **keeps loading the publisher's COM DLL into the elevated
installer process** and invoking its `DllRegisterServer` export there
(`src/SigilBuild.Wrapper.Core/Steps/Win32/ComRegistration.cs`,
`Invoke`). It does **not** shell out to `regsvr32.exe`.

The reason is not that isolation would be expensive. It is that **a child
process would not be an isolation boundary at all.** A child spawned by the
installer inherits the installer's token, so `regsvr32.exe` would run at exactly
the same integrity level, with exactly the same ability to write
`HKLM\Software\Classes` — which is the whole point of the step. Moving the load
out of process converts an *availability* problem (a buggy DLL kills the
installer) into a slightly smaller availability problem, and converts a
*security* problem (a hostile DLL runs as administrator) into precisely the same
security problem in a different process. Paying `regsvr32`'s real costs — bitness
selection, unusable exit codes, a LOLBIN invocation in every Sigil-built
installer — for that trade is not worth it.

The trust assumption being accepted, stated plainly:

> **A `com_register` step is an explicit grant of arbitrary code execution as
> administrator to the DLL named by `path`.** Sigil guarantees only that the DLL
> is the one the publisher shipped, sitting where only administrators can have
> put it. It does not, and cannot, guarantee anything about what that DLL's
> `DllRegisterServer` does once called.

That assumption is now written where publishers read it
(`docs/guides/install-steps.md`, `com_register`), not only where maintainers do.

---

## Context

### What the step does today

`ComRegisterStep.RunAsync`:

1. resolves `path` through `StepContext.ResolvePath` (`${…}` / `{…}` expansion,
   unresolved-token refusal, `payload://` traversal guard);
2. runs `PrivilegedTargetGuard.Check`, which requires the resolved path to sit
   **inside `install_dir`**, with no directory junction on the way down, **and**
   in a directory that only `NT AUTHORITY\SYSTEM`, `BUILTIN\Administrators` or
   `NT SERVICE\TrustedInstaller` can write (register rows R3/R9);
3. journals the inverse (`RollbackRecord.UnregisterCom`) *before* registering;
4. calls `ComRegistration.Invoke(path, "DllRegisterServer")`, which is
   `LoadLibraryExW(..., LOAD_WITH_ALTERED_SEARCH_PATH)` →
   `GetProcAddress` → a call through a C# unmanaged function pointer
   (`delegate* unmanaged[Stdcall]<int>`) → `FreeLibrary` in a `finally`.

Step 4 is the one this ADR is about. Steps 1–3 are unchanged by it and are
load-bearing for the analysis below.

### Why the row was raised

The plain reading of "the installer calls `LoadLibrary` on a third-party DLL and
invokes an export" is alarming, and correctly so as a *description*. The
question this ADR has to answer is narrower: **would running that same call in a
child process make anything materially safer?**

---

## Alternatives considered

### A. Keep the in-process load (chosen)

The DLL is loaded and its export called inside the installer process.

- The publisher's code runs with the installer's token: **administrator**.
- A DLL that faults takes the installer with it. Because the fault is a process
  death rather than a returned failure, the rollback journal does **not** replay:
  the install is left half-applied, and recovery is the user re-running
  `Setup.exe` (whose reinstall path replays the recorded uninstall first) or
  running `Setup.exe /Uninstall`, both of which consume the journal written to
  disk before the crash.
- Nothing new is spawned, so there is no new binary to locate, no new bitness
  question, and no new process-creation surface.

### B. Invoke `regsvr32.exe` in a child process (rejected)

The classic shape: `%SystemRoot%\System32\regsvr32.exe /s <dll>`, wait, map the
exit code.

Rejected because:

1. **It is not a privilege boundary.** `Process.Start` from an elevated parent
   produces an equally elevated child at the same integrity level. The hostile-DLL
   case — the one that motivates the row — ends with attacker code running as
   administrator either way. Constructing a genuinely lower-privilege host
   (`CreateProcessAsUser` with a restricted or medium-integrity token) is not
   available: `DllRegisterServer` writes machine-global `HKLM\Software\Classes`
   registration and would simply fail, which is why `com_register` is
   machine-scope-only (**SIG0310**) in the first place. The isolation this
   alternative promises is unbuildable for this specific operation.
2. **Bitness.** `regsvr32.exe` must match the DLL's architecture, and the
   installer cannot tell a 32-bit COM DLL from a 64-bit one without parsing its
   PE header — new code whose only purpose is to select between `System32` and
   `SysWOW64`. Choosing wrong produces a confusing failure at install time on the
   user's machine rather than at pack time on the publisher's.
3. **Exit codes.** `regsvr32`'s exit codes are famously coarse and, with `/s`, its
   diagnostics go nowhere. The current path returns the actual `HRESULT` from
   `DllRegisterServer` and distinguishes "DLL or a dependency would not load"
   (`LoadFailed`, with the Win32 error), "not a self-registering COM DLL"
   (`ExportMissing`), and "registration failed" (`HResultFailure`, with the
   HRESULT) — three genuinely different publisher-facing errors that collapse into
   one under `regsvr32`.
4. **It is a LOLBIN.** `regsvr32.exe` is among the most heavily-flagged
   living-off-the-land binaries in endpoint detection. Making every Sigil-built
   installer that registers COM spawn it converts a legitimate operation into an
   EDR alert on every customer machine.
5. **It adds a target of its own.** The child's own image path has to be resolved
   absolutely and defended, re-introducing a binary-planting question the
   in-process path does not have.

What it would genuinely buy: **crash isolation and a timeout.** A hung or
faulting `DllRegisterServer` would fail the step instead of killing the install.
That is a real benefit, and it is the only one. It is not worth items 1–5, and it
is not the benefit the register row asked for.

### C. Keep in-process, add a structured-exception guard (rejected)

Wrapping the call so a native access violation becomes a step failure. Rejected
because .NET does not offer a supported, AOT-safe way to do this: corrupted-state
exceptions are not catchable on .NET Core by design, and the vectored-exception-handler
route is exactly the sort of runtime machinery `PublishAot` / `TrimMode=full` and
this repo's AOT rules exist to keep out. A guard that catches *some* faults and
silently misses others would be worse than none, because it would read as
protection.

---

## Decision detail

### What is relied on instead of isolation

The security of `com_register` rests on **provenance of the DLL**, not on
containment of its behaviour:

| Control | Where | What it stops |
|---|---|---|
| Path substitution + unresolved-token refusal | `StepContext.ResolvePath` | A typo'd or partially-substituted path resolving to something unintended |
| `install_dir` containment, junction-aware | `PrivilegedTargetGuard.Check` | A DLL outside the installed application; a junction planted inside it redirecting the load |
| Admin-only-writable directory requirement | `PrivilegedTargetGuard.Check` → `StateDirectorySecurity` | A **non-administrator** planting or replacing the DLL — the escalation case |
| Machine-scope-only (SIG0310) | `MachineScopeGuard`, pack time | The step appearing in a per-user install, where the containing directory is user-writable by construction |
| `payload://` refusal | `PrivilegedTargetGuard` remarks | Loading from the user-writable extraction temp directory |

Together these mean the DLL that gets loaded is one an administrator put inside
the installed application. **The residual trust is in the publisher**, and a
publisher who wanted to run code as administrator during their own install has
`run_program` and needs no COM DLL to do it. `com_register` is therefore not an
*additional* grant of authority to the publisher; it is the same authority the
manifest already carries, exercised through a different verb.

### What is accepted as a known limitation

- **A faulting or hanging `DllRegisterServer` kills or wedges the install.**
  There is no timeout and no crash isolation. The journal is on disk before the
  call, so the state is recoverable, but the run itself is lost.
- **`FreeLibrary` does not undo everything.** A DLL that spawned a thread,
  installed a hook, or patched process state during `DllMain`/`DllRegisterServer`
  keeps those effects for the remaining life of the installer process.

### What would reverse this decision

Any one of these, and this ADR should be superseded:

1. **`com_register` gains a non-publisher-authored input.** If `path` ever
   becomes reachable from a wizard field, a `registry_read` var, a `/P<name>=`
   argument or a downloaded artifact, the DLL is no longer "what the publisher
   shipped" and provenance stops carrying the argument.
2. **A real sandbox becomes available for the operation.** An AppContainer or
   restricted-token host that can still complete machine-global COM registration
   — via a broker, or via Windows offering a supported alternative to
   self-registration — turns alternative B into an actual privilege boundary.
3. **Crash rates make availability the dominant concern.** If real publishers'
   DLLs are observed faulting during install, alternative B's one genuine benefit
   becomes the deciding one, and the bitness/exit-code costs become worth paying.

---

## Consequences

**Positive.**

- No new process, no bitness detection, no `regsvr32` dependency, no LOLBIN
  invocation in shipped installers.
- The publisher-facing error messages stay specific (load failure with Win32
  error / missing export / HRESULT) rather than collapsing into `regsvr32`'s
  exit-code soup.
- The AOT posture is unchanged and remains the strong one: `[LibraryImport]`
  source-generated stubs plus a statically-bound
  `delegate* unmanaged[Stdcall]<int>` — no reflection, no runtime IL, no
  `Marshal.GetDelegateForFunctionPointer`. This is the one AOT-risk step
  identified in P11 and it stays resolved.

**Negative, accepted.**

- A malformed publisher DLL can crash the installer. Documented, not mitigated.
- There is no execution timeout on `DllRegisterServer`.

**Neutral.**

- The trust assumption is now stated in `docs/guides/install-steps.md` under
  `com_register`, so a publisher reading the guide sees what the step grants
  before writing it into a manifest.

---

## Verification

- `ComRegisterStepTests` and `PrivilegedStepContainmentTests` pin the
  provenance controls the decision rests on: a `com_register` whose `path`
  escapes `install_dir`, reaches it through a junction, or lands in a
  non-admin-only-writable directory is refused **before** the journal entry and
  before any load.
- `docs/guides/install-steps.md`'s `com_register` section states the trust
  assumption and links back to the anchoring rules.
- No behavioural change ships with this ADR — that is the point of it. The
  decision is to keep the current implementation, and the deliverable is the
  written rationale plus the publisher-facing statement.

---

## Amendment log

| Date | Change |
|---|---|
| 2026-08-11 | Initial version. Stage 2, lane S6, register row R36. |

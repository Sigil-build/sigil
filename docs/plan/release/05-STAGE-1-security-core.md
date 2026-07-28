# Stage 1 — Security core

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> Global constraints live in [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints)
> and apply to every task here.

**Goal:** Close all five local privilege-escalation paths, and make the test
suite stop reporting green for work it never did.

**Architecture:** Four file-disjoint lanes in parallel. S1 owns the on-disk and
registry trust boundary, S2 owns path containment, S3 owns everything staged to
disk and then executed, T1 owns whether any of it is actually proven.

**Tech Stack:** .NET 10, Win32 P/Invoke via `[LibraryImport]`, xUnit +
FluentAssertions, GitHub Actions.

**Runs after:** Gate G0. **Merge order at G1:** S1 → S2 → S3 → T1.

| Lane | Model | Branch | Findings |
|---|---|---|---|
| `S1` trusted state | opus-5 | `rc/s1-trusted-state` | R1, R2, R19 |
| `S2` path containment | opus-5 | `rc/s2-path-containment` | R3, R9, R16, R31, R32 |
| `S3` staged execution | opus-5 | `rc/s3-staged-execution` | R4, R5, R10, R11, R12, R17 |
| `T1` test truth | sonnet-5 | `rc/t1-test-truth` | R6, R21, R22 |

**Cross-lane rules:** `InstallSession.cs` is S1's alone — S3 reports rather than
edits. `ci.yml` is T1's alone. Security lanes add **new** test files freely but
must not touch the gating constructs in the eight VM-gated classes (T1 owns
those).

---

## A note on test-writing in this stage

The repo's own house style contains the bug this stage fixes. You will see:

```csharp
if (!OperatingSystem.IsWindows())
{
    return;                     // ← reports as PASSED
}
```

**Do not copy that pattern into new tests.** Use `Assert.Skip` (xUnit v3 is
available — `xunit.v3` 3.2.2 is pinned in `Directory.Packages.props`):

```csharp
Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only: registry/ACL APIs");
```

T1 converts the existing ones; do not create new debt in the meantime.

---

# Lane S1 — trusted state

**Findings:** R1 (elevated replay of user-writable state, on install *and*
uninstall), R2 (elevated spawn of an HKCU-sourced exe), R19 (hostile JSON
crashes the elevated process).

**Read first:** register rows R1, R2, R19 in full, then
`Engine/UninstallStateStore.cs`, `RollbackJournal.cs`, `UninstallEngine.cs`,
`InstalledStateResolver.cs`, `InstallSession.cs:605-630` and `:905-975`,
`ScopeLayout.cs`, `Json/SerializableRollbackRecord.cs`.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `src/SigilBuild.Wrapper.Core/Engine/StateDirectorySecurity.cs` | Create | create the machine state dir with an admin-only DACL; answer "is this path admin-owned?" |
| `Engine/UninstallStateStore.cs` | Modify | drop the opposite-scope fallback; derive scope from location; gate load on ownership; bound the read |
| `Engine/RollbackJournal.cs` | Modify | anchor every replayed path and registry coordinate |
| `Engine/InstalledStateResolver.cs` | Modify | HKLM-only when the scope is machine |
| `Engine/InstallSession.cs` | Modify | verify the prior uninstaller before spawning it |
| `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs` | Create | the negative tests for all of the above |

---

## Task S1.1: Refuse state from a directory a non-admin can write

**Files:**
- Create: `src/SigilBuild.Wrapper.Core/Engine/StateDirectorySecurity.cs`
- Create: `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`
- Modify: `src/SigilBuild.Wrapper.Core/Engine/UninstallStateStore.cs:70-71` (creation), `:130-178` (load)

**Interfaces:**
- Consumes: `ScopeLayout.For(scope).StateRoot`
- Produces — later tasks and lanes rely on exactly these:
  ```csharp
  namespace SigilBuild.Wrapper.Engine;

  internal static class StateDirectorySecurity
  {
      /// Creates <paramref name="path"/> with SYSTEM + Administrators FullControl,
      /// Users Read, inheritance disabled. No-op if it already exists and passes
      /// IsTrusted. Throws UnauthorizedAccessException if it exists and does not.
      public static void CreateHardened(string path);

      /// True when the directory exists and its owner is LocalSystem or
      /// BUILTIN\Administrators. False on any error — fail closed.
      public static bool IsTrusted(string path);

      /// True when only SYSTEM and Administrators can write the directory
      /// CONTAINING <paramref name="path"/>. Used by S2 to gate SYSTEM-level
      /// step targets and by S3 to site its staging directories.
      /// False on any error — fail closed.
      public static bool IsAdminOnlyWritable(string path);
  }
  ```

  > **Cross-lane:** `IsAdminOnlyWritable` is consumed by **S2** (Task S2.3) and
  > **S3** (Task S3.1), which run in parallel with this lane. S1 is first in the
  > G1 merge order precisely so this lands first. Implement it in **this task**,
  > push it early, and tell the orchestrator the moment it is on the branch so
  > S2 and S3 can rebase. Do not let the other two lanes duplicate ACL logic —
  > three implementations of "who can write here" is how the containment bugs
  > got here in the first place.

- [ ] **Step 1: Write the failing test**

Create `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`:

```csharp
namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

/// <summary>
/// R1: the machine-scope state directory is the trust boundary for elevated
/// replay. These tests assert it is refused when an unprivileged user could
/// have authored it.
/// </summary>
public class StateProvenanceTests
{
    [Fact]
    public void Untrusted_state_directory_is_not_trusted()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACL APIs");

        // Arrange — a directory created with default (inherited, user-owned)
        // ACLs under the test temp dir, exactly like the current bare
        // Directory.CreateDirectory does under %ProgramData%.
        using var temp = new TempDir();
        var dir = Path.Combine(temp.Path, "sigil-state");
        Directory.CreateDirectory(dir);

        // Act
        var trusted = StateDirectorySecurity.IsTrusted(dir);

        // Assert
        trusted.Should().BeFalse(
            "a directory owned by the current (non-SYSTEM) user must never be " +
            "trusted to supply records for elevated replay");
    }

    [Fact]
    public void IsTrusted_fails_closed_on_a_missing_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ACL APIs");

        using var temp = new TempDir();

        StateDirectorySecurity
            .IsTrusted(Path.Combine(temp.Path, "does-not-exist"))
            .Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "FullyQualifiedName~StateProvenanceTests"
```

Expected: **compile error** — `StateDirectorySecurity` does not exist. That is
the correct first failure.

- [ ] **Step 3: Implement `StateDirectorySecurity`**

Create the file. Use `System.Security.AccessControl` /
`System.Security.Principal` (`DirectorySecurity`, `DirectoryInfo.GetAccessControl`,
`WellKnownSidType.LocalSystemSid`, `WellKnownSidType.BuiltinAdministratorsSid`).
Guard the whole type with `[SupportedOSPlatform("windows")]` — the codebase
already uses that attribute (see `InstalledStateResolver.cs`) and it is required
to keep the build warning-free.

`CreateHardened` must call `DirectorySecurity.SetAccessRuleProtection(true, false)`
so the permissive `%ProgramData%` inheritance is **discarded, not merged** —
merging is the whole bug. `IsTrusted` reads the owner via
`GetAccessControl().GetOwner(typeof(SecurityIdentifier))` and compares against
the two well-known SIDs, returning `false` from a `catch` rather than throwing.

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "FullyQualifiedName~StateProvenanceTests"
```

Expected: **2 passed**.

- [ ] **Step 5: Wire it into `UninstallStateStore`**

At the creation site (`:70-71`), replace the bare `Directory.CreateDirectory(dir)`
with `StateDirectorySecurity.CreateHardened(dir)` **for machine scope only** —
user scope legitimately lives in the user's own profile and hardening it would
be meaningless.

At the load site, refuse untrusted state before deserializing:

```csharp
if (scope == InstallScope.Machine && !StateDirectorySecurity.IsTrusted(dir))
{
    // R1: an unprivileged user can pre-create %ProgramData%\Sigil\<AppId> and
    // become CREATOR OWNER of the file the elevated uninstall later replays.
    // Refuse rather than replay, and say so — a silent skip here reads as
    // "no prior install" and would mask an attack.
    InstallLog.Warn($"refusing state in '{dir}': not owned by SYSTEM or Administrators");
    continue;
}
```

- [ ] **Step 6: Verify and commit**

```bash
dotnet build Sigil.slnx -c Release && dotnet test Sigil.slnx -c Release
git add -A && git commit -m "fix(security): harden and verify the machine state directory (R1)

%ProgramData%\\Sigil\\<AppId> was created with a bare Directory.CreateDirectory,
inheriting ProgramData's default ACL, which grants BUILTIN\\Users write and
makes the creating user CREATOR OWNER. An unprivileged user could pre-create
it and own the uninstall.json that an elevated uninstall later replays.

Creates it with an explicit non-inherited DACL (SYSTEM + Administrators full,
Users read) and refuses to load machine-scope state from a directory not owned
by SYSTEM or Administrators."
```

---

## Task S1.2: Never cross scopes, and never trust the scope written inside the file

**Files:**
- Modify: `Engine/UninstallStateStore.cs:136-140` (the fallback), `:174` (scope source)
- Modify: `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`

**Interfaces:**
- Consumes: `StateDirectorySecurity.IsTrusted` from S1.1
- Produces: `TryLoad(appId, scope)` that reads **only** `scope`'s directory and
  returns a `LoadedState` whose `Scope` is the directory's scope, not the file's

- [ ] **Step 1: Write the failing test**

Append to `StateProvenanceTests`:

```csharp
    [Fact]
    public void Machine_scope_load_ignores_state_planted_in_the_user_scope()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only state layout");

        // Arrange — write user-scope state, the location an unprivileged user
        // fully controls, and claim machine scope from inside the file.
        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        var journal = new RollbackJournal();
        journal.Append(new RollbackRecord.RemoveDirectory(@"C:\Windows\System32\evil"));
        UninstallStateStore.Save(appId, InstallScope.User, journal);

        try
        {
            // Act — an elevated /allusers uninstall asks for machine scope.
            var loaded = UninstallStateStore.TryLoad(appId, InstallScope.Machine);

            // Assert — the user-scope file must be invisible to it. Before this
            // fix, TryLoad fell through to the opposite scope and then took the
            // authoritative scope from a field inside the attacker's own file.
            loaded.Should().BeNull(
                "a machine-scope operation must never read %LocalAppData%");
        }
        finally
        {
            UninstallStateStore.Delete(appId, InstallScope.User);
        }
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "Machine_scope_load_ignores"
```

Expected: **FAIL** — `loaded` is non-null, because `:136-140` searches
`AllScopes` when the preferred scope is `Machine`.

- [ ] **Step 3: Delete the fallback and the in-file scope**

Replace the scope-ordering array with a single-element lookup of the requested
scope. Then change the return so `LoadedState.Scope` is the scope **whose
directory the file was found in**, not `s.Scope` read from the deserialized
document. Remove the now-dead `Scope` field read; leave the serialized field in
place for backward compatibility but stop consuming it, and add a comment saying
why it must never be consumed again.

- [ ] **Step 4: Run the full suite**

```bash
dotnet test Sigil.slnx -c Release
```

Expected: the new test passes and **no existing test regresses**. If an existing
test depended on the cross-scope fallback, read it carefully — it is asserting
the vulnerability, and it should be rewritten to assert the refusal.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "fix(security): drop the opposite-scope state fallback (R1)

TryLoad searched the preferred scope's directory and then the OPPOSITE
scope's, returning the first uninstall.json it found and taking the
authoritative scope from a field inside that file. A user could plant state
in %LocalAppData% and have an elevated /allusers uninstall replay it.

Machine scope now reads only the machine directory, and the scope comes from
the directory the file was found in."
```

---

## Task S1.3: Anchor every replayed record

**Files:**
- Modify: `Engine/RollbackJournal.cs` — `RestoreFile` (:156-164), `RemoveDirectory` (:174-177), `DeleteShortcut` (:192-197), `RestoreRegistryValue` (:238-266), `RestoreRegistryKey` (:359-373), `RestoreEnv` (:299-325), `RestoreDeletedFile`/`RestoreDeletedDirectory` (:388-423), `RestoreConfigFile` (:457-476), `RemoveUninstaller` (:494), `RemoveService` (:510-511), `DeleteScheduledTask` (:558-561), `UnregisterCom` (:602), `DeleteFirewallRule` (:640)
- Modify: `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`

**Interfaces:**
- Consumes: the recorded `install_dir` for the run, plus
  `ScopeLayout.For(scope)` roots
- Produces: `UndoAsync` that skips-and-logs any record whose target escapes the
  anchor set, and a per-record outcome S5 will later surface (R15)

- [ ] **Step 1: Write the failing test — start with the worst primitive**

```csharp
    [Fact]
    public async Task Replay_refuses_a_com_record_pointing_outside_the_install_dir()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only COM registration");

        using var installDir = new TempDir();
        var journal = new RollbackJournal();

        // The single worst record in the catalogue: LoadLibrary + call an
        // export from an attacker-chosen path, inside the elevated process.
        journal.Append(new RollbackRecord.UnregisterCom(@"C:\Users\Public\evil.dll"));

        var outcome = await journal.UndoAsync(
            System.Threading.CancellationToken.None,
            progress: null,
            installDir: installDir.Path);

        outcome.RefusedRecords.Should().ContainSingle()
            .Which.Should().Contain("evil.dll",
                "a DLL outside install_dir must never be loaded by the elevated process");
    }
```

> **Interface note:** this adds an `installDir` parameter and a
> `RefusedRecords` collection to `UndoAsync`'s return. Both are new. Update
> `UninstallEngine.cs:50` and any other caller; the compiler will find them.
> S5 consumes `RefusedRecords` in Stage 2 for R15 — keep the name.

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "Replay_refuses_a_com_record"
```

Expected: compile error on the new parameter — then, once wired, FAIL because
nothing refuses anything today.

- [ ] **Step 3: Implement anchoring**

Add a private predicate used by every path-bearing record: a target is allowed
when it is contained in the recorded `install_dir`, **or** in an explicit
allowlist of scope roots the installer legitimately writes (start-menu and
desktop shortcut folders, the per-app state dir, the PATH environment key). Use
`Path.GetFullPath` **before** comparing, and terminate the root with a directory
separator so `C:\rootevil` cannot pass as `C:\root`.

For registry records, require the key to sit under the app's own subtree
(`…\Uninstall\<AppId>`, or the app's own `Software\<Publisher>\<App>` key) — not
merely "under HKLM".

For `UnregisterCom`, **re-derive** the DLL path from `install_dir` plus the
recorded relative name rather than trusting the persisted absolute path.

A refused record is **logged and skipped**, added to `RefusedRecords`, and does
not abort the rest of the replay. Silent skipping would mask an attack; aborting
would let one planted record block a legitimate uninstall.

- [ ] **Step 4: Extend the test to the other primitives**

Add cases asserting refusal for: `RestoreFile` writing to
`C:\Windows\System32\`, `RestoreRegistryValue` writing to
`HKLM\SYSTEM\CurrentControlSet\Services\`, `RestoreEnv` with `scope: machine`,
and `RemoveService` naming a service the app never installed. Then add a
**positive** case proving a legitimate in-`install_dir` record still replays —
anchoring that breaks real uninstalls is worse than the bug.

- [ ] **Step 5: Run the full suite and commit**

```bash
dotnet test Sigil.slnx -c Release
git add -A && git commit -m "fix(security): anchor rollback replay to install_dir (R1)

Journal records carried absolute paths and full registry coordinates with no
anchoring, so a planted journal gave the elevated process arbitrary file
write/delete, arbitrary HKLM write, machine PATH hijack, service deletion,
and — via unregister_com — LoadLibrary plus an export call on an
attacker-chosen DLL.

Replay now refuses any record whose target escapes install_dir or the scope
roots the installer legitimately writes, logs the refusal, and continues."
```

---

## Task S1.4: HKLM-only probing, and verify the prior uninstaller before spawning it

**Files:**
- Modify: `Engine/InstalledStateResolver.cs:38-40`
- Modify: `Engine/InstallSession.cs:937-975`
- Modify: `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`

**Interfaces:**
- Consumes: `AuthenticodeVerifier.VerifyFile(string path)` — already exists at
  `Engine/AuthenticodeVerifier.cs:63` and is currently called from only one
  place
- Produces: `UpgradeState` resolution that cannot be steered from HKCU

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Machine_scope_resolve_ignores_an_HKCU_arp_entry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows registry");

        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        using var planted = TestRegistry.PlantUserUninstallEntry(
            appId,
            displayVersion: "0.0.1",
            uninstallString: @"C:\Users\Public\evil.exe /S /Uninstall");

        var state = InstalledStateResolver.Resolve(appId, InstallScope.Machine);

        state.Should().Be(UpgradeState.None,
            "a machine-scope resolve must not read the user hive — HKCU is " +
            "writable by the unprivileged user whose exe would then be spawned " +
            "by the elevated installer");
    }
```

> `TestRegistry` lives at `tests/SigilBuild.Wrapper.Tests/Helpers/TestRegistry.cs`.
> Read it first; if it has no `PlantUserUninstallEntry`, add one following the
> existing helpers' disposal pattern so the key is always cleaned up.

- [ ] **Step 2: Run it and watch it fail**

Expected: FAIL — `Resolve` returns a populated `UpgradeState` from HKCU.

- [ ] **Step 3: Make machine scope probe HKLM only**

Change the scope-ordering array at `:38-40` so `Machine` yields
`new[] { InstallScope.Machine }`. User scope may keep its fallback: a user-scope
install reading HKLM is reading a hive it cannot write, which is safe.

- [ ] **Step 4: Verify the prior uninstaller before spawning**

At `InstallSession.cs:937`, after the existing `File.Exists` check and before
`Process.Start`, require the exe to be Authenticode-valid **or** to live under a
directory only administrators can write. Refuse with a clear message otherwise —
do **not** silently continue, because silently continuing is the current
behaviour and the reason this is exploitable.

- [ ] **Step 5: Add the spawn-refusal test, verify, commit**

```csharp
    [Fact]
    public async Task Prior_uninstaller_in_a_user_writable_path_is_not_spawned()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only elevation path");

        using var temp = new TempDir();
        var fakeUninstaller = Path.Combine(temp.Path, "evil.exe");
        File.WriteAllBytes(fakeUninstaller, new byte[] { 0x4D, 0x5A });   // "MZ"

        var outcome = await InstallSession.RunPriorUninstallForTestAsync(
            fakeUninstaller, InstallScope.Machine);

        outcome.Succeeded.Should().BeFalse();
        outcome.Message.Should().Contain("not verified");
    }
```

> This needs a narrow `internal` test seam on `InstallSession`. Add it beside
> the existing seams and expose it via the project's existing
> `InternalsVisibleTo` — do not make the production method public.

```bash
dotnet build Sigil.slnx -c Release && dotnet test Sigil.slnx -c Release
git add -A && git commit -m "fix(security): HKLM-only machine probe, verify prior uninstaller (R2)

A machine-scope install probed HKCU when HKLM held no entry, then spawned
that entry's UninstallString from the already-elevated process with no
signature check and no path validation. A standard user could plant an HKCU
ARP entry and have the publisher's signed installer run their binary as admin.

Machine scope now reads only HKLM, and a prior uninstaller must be
Authenticode-valid or admin-path-resident before it is spawned."
```

---

## Task S1.5: Hostile JSON fails closed instead of crashing

**Files:**
- Modify: `Engine/UninstallStateStore.cs:148` (unbounded read), `:150-171` (narrow try)
- Modify: `tests/SigilBuild.Wrapper.Tests/Engine/StateProvenanceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Theory]
    [InlineData("{\"records\":[{\"type\":\"no-such-type\"}]}")]
    [InlineData("{\"records\":[null]}")]
    [InlineData("{\"records\":[{\"type\":\"restore_file\"}]}")]   // required field missing
    public void Hostile_state_json_is_refused_without_an_unhandled_exception(string json)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only state layout");

        var appId = "sigil.test." + Guid.NewGuid().ToString("N");
        var dir = UninstallStateStore.DirectoryForTest(appId, InstallScope.User);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "uninstall.json"), json);

        try
        {
            var act = () => UninstallStateStore.TryLoad(appId, InstallScope.User);

            act.Should().NotThrow(
                "rehydration happens OUTSIDE the try today, so a one-line planted " +
                "file makes every install and uninstall of this AppId die");
            act().Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
```

- [ ] **Step 2: Run it and watch it fail**

Expected: FAIL — `InvalidOperationException` from
`SerializableRollbackRecord.ToRollbackRecord` (`:288-289`, `:376-377`) or
`ArgumentNullException` (`:223`), none of which the `catch { continue; }` at
`:157-160` covers.

- [ ] **Step 3: Widen the try and bound the read**

Move the record-rehydration loop inside the `try`, treating any failure as
"state unreadable" with a logged reason. Before `File.ReadAllText`, check
`new FileInfo(path).Length` against a constant ceiling (a few MB is generous for
a journal) and refuse above it; after deserializing, refuse an implausible
record count. Both bounds get a named constant with a comment.

- [ ] **Step 4: Verify and commit**

```bash
dotnet test Sigil.slnx -c Release
git add -A && git commit -m "fix(security): hostile uninstall.json fails closed, not fatally (R19)

Record rehydration sat outside TryLoad's catch, so an unknown discriminator,
a null record, or a missing required field threw an unhandled exception —
making a one-line planted file a persistent per-app install/uninstall DoS.
File size and record count were also unbounded.

Rehydration is now inside the try and both bounds are enforced."
```

---

## Lane S1 definition of done

- [ ] Build clean, suite green, format clean
- [ ] Each negative test confirmed **failing on the parent commit**, and you say
      so explicitly in the PR
- [ ] Manual, as a standard user: plant `uninstall.json` in `%ProgramData%` and
      in `%LocalAppData%`, plant an HKCU ARP entry — report what the log said
- [ ] PR into `release/v0.1.0-alpha`

---

# Lane S2 — path containment

**Findings:** R3 (`/D=` unvalidated + privileged targets unanchored), R9 (`/P`
values into privileged fields), R16 (no containment on any step destination),
R31 (`/TR` quoting), R32 (INI line injection).

**Read first:** rows R3, R9, R16, R31, R32; `Engine/InstallDirResolver.cs:50-110`,
`Engine/StepContext.cs:357`, `:399-440`, `:502-540`, `ScopeLayout.cs`; the four
privileged steps; `ConfigFileEditor.cs`, `IniWriteStep.cs`, `FileCopyStep.cs`,
`FileDeleteStep.cs`, `DirectoryDeleteStep.cs`, `HttpDownloadStep.cs`;
`docs/guides/install-steps.md:185-235`.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `Engine/PathContainment.cs` | Create | the single containment helper |
| `Engine/InstallDirResolver.cs` | Modify | reject an install_dir outside the scope root |
| `Engine/StepContext.cs` | Modify | fail on unresolved brace tokens in path fields |
| `Steps/{ScheduledTaskCreate,ServiceInstall,FirewallRule}Step.cs`, `Steps/Win32/ComRegisterStep.cs` | Modify | anchor privileged targets; reject non-admin-writable dirs |
| `Steps/{ConfigFileEditor,FileCopy,FileDelete,DirectoryDelete,HttpDownload}Step.cs` | Modify | route destinations through the helper |
| `Steps/IniWriteStep.cs` | Modify | reject CR/LF and leading `[` |
| `tests/SigilBuild.Wrapper.Tests/Engine/PathContainmentTests.cs` | Create | containment negative tests |

---

## Task S2.1: The containment helper

**Interfaces:**
- Produces — S2's later tasks and S3 both use this:
  ```csharp
  namespace SigilBuild.Wrapper.Engine;

  internal static class PathContainment
  {
      /// True when <paramref name="candidate"/> resolves inside <paramref name="root"/>.
      /// Canonicalises both with Path.GetFullPath BEFORE comparing, terminates the
      /// root with a directory separator so "C:\rootevil" cannot pass as "C:\root",
      /// and compares OrdinalIgnoreCase. Returns false on any exception.
      public static bool IsUnder(string root, string candidate);

      /// IsUnder, plus: no component of the path from root to candidate is a
      /// reparse point (junction or symlink). Directory junctions need no
      /// privilege on Windows, so this is the check that stops redirection.
      public static bool IsUnderWithoutTraversal(string root, string candidate);

  }
  ```

  > **Cross-lane:** the "who can write this directory" check lives in **S1**'s
  > `StateDirectorySecurity.IsAdminOnlyWritable` (Task S1.1), not here. Rebase
  > on S1's merge before Task S2.3 and consume it. Do not add a second ACL
  > implementation.

- [ ] **Step 1: Write the failing test**

Create `tests/SigilBuild.Wrapper.Tests/Engine/PathContainmentTests.cs`:

```csharp
namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using System.IO;
using FluentAssertions;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

public class PathContainmentTests
{
    [Theory]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App\bin\a.exe", true)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App", true)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\App\..\Other\a.exe", false)]
    [InlineData(@"C:\Program Files\App", @"C:\Windows\System32\a.dll", false)]
    [InlineData(@"C:\Program Files\App", @"C:\Program Files\AppEvil\a.exe", false)]
    [InlineData(@"C:\Program Files\App", @"\\server\share\a.exe", false)]
    public void IsUnder_contains_only_real_descendants(string root, string candidate, bool expected)
        => PathContainment.IsUnder(root, candidate).Should().Be(expected);

    [Fact]
    public void IsUnderWithoutTraversal_rejects_a_directory_junction_in_the_chain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junctions");

        using var root = new TempDir();
        using var outside = new TempDir();
        var link = Path.Combine(root.Path, "link");

        // Directory junctions require no privilege — this is the realistic
        // redirection primitive, not symlinks.
        Directory.CreateSymbolicLink(link, outside.Path);

        var target = Path.Combine(link, "config.json");

        PathContainment.IsUnder(root.Path, target).Should().BeTrue(
            "the textual path still looks contained");
        PathContainment.IsUnderWithoutTraversal(root.Path, target).Should().BeFalse(
            "following the junction escapes the root, which is the actual bug");
    }
}
```

The `AppEvil` and junction cases are the two that matter — a naive
`StartsWith` implementation passes every other row and fails those.

- [ ] **Step 2: Run it and watch it fail** — compile error, `PathContainment` absent.

- [ ] **Step 3: Implement it**

Lift the proven logic from `StepContext.cs:527-532` (canonicalise, then
separator-terminated `OrdinalIgnoreCase` prefix compare). Add reparse-point
detection by walking from `root` to `candidate` and testing
`File.GetAttributes(component).HasFlag(FileAttributes.ReparsePoint)`.

**Do not modify the existing `payload://` or zip-slip guards to route through
this helper in this task.** They are verified sound; refactoring them is
gratuitous risk during a security fix. Leave them, and note the duplication for
a post-v1 cleanup.

- [ ] **Step 4: Run and watch pass. Step 5: Commit.**

```bash
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "PathContainmentTests"
git add -A && git commit -m "feat(security): add the PathContainment helper (R16)"
```

---

## Task S2.2: Reject an out-of-root `install_dir` — the `/D=` fix

**Files:**
- Modify: `Engine/InstallDirResolver.cs:66-69`
- Create: `tests/SigilBuild.Wrapper.Tests/Engine/InstallDirContainmentTests.cs`

This single change is the highest-value line in the lane.

- [ ] **Step 1: Write the failing test**

```csharp
namespace SigilBuild.Wrapper.Tests.Engine;

using System;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using Xunit;

public class InstallDirContainmentTests
{
    [Fact]
    public void Machine_scope_rejects_a_cli_override_outside_the_scope_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows scope roots");

        // Setup.exe /allusers /D=C:\Users\Public\evil — an admin approves the
        // UAC prompt for a legitimately signed installer, and a SYSTEM-level
        // scheduled task or service then points at a user-writable directory.
        var act = () => InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: @"C:\Users\Public\evil");

        act.Should().Throw<InstallDirRejectedException>()
           .WithMessage("*outside*");
    }

    [Fact]
    public void Machine_scope_accepts_a_path_under_the_scope_root()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows scope roots");

        var root = ScopeLayout.For(InstallScope.Machine).InstallRoot;

        var resolved = InstallDirResolver.Resolve(
            InstallScope.Machine,
            appName: "MyApp",
            appId: "com.example.myapp",
            manifestInstallDir: null,
            cliOverride: System.IO.Path.Combine(root, "MyApp"));

        resolved.Should().StartWith(root);
    }
}
```

> **Interface note:** `InstallDirRejectedException` is new. Put it beside
> `InstallDirResolver`. The wizard and the silent path must both render its
> message as an install failure rather than letting it escape as an unhandled
> exception — check both call sites.

- [ ] **Step 2: Run it and watch the first case fail** (today it returns the
      path happily) **and the second pass** (proving the test is not vacuous).

- [ ] **Step 3: Implement the check**

After `Canonicalize`, if `scope == InstallScope.Machine` and the result is not
`PathContainment.IsUnderWithoutTraversal(ScopeLayout.For(scope).InstallRoot, resolved)`,
throw. Apply it to **every** source — `collected` (wizard), `cliOverride`
(`/D=`), `priorInstallDir` (recovered state, R1's neighbour), and
`manifestInstallDir` — not just `/D=`. A manifest author pointing at
`C:\Users\Public` is the same hole.

For user scope, contain to the user's own root. A user writing inside their own
profile is not an escalation, but an unanchored user-scope install still lets a
manifest write anywhere the user can, so keep the check and only widen the root.

- [ ] **Step 4: Run the full suite**

Expect fallout: existing tests may pass arbitrary temp paths as `install_dir`.
Those are legitimate test fixtures, not attacks — add an `internal` test-only
escape hatch (e.g. an optional `allowAnyRoot` parameter defaulted to `false`,
used only by tests) rather than weakening the production rule.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "fix(security): reject an install_dir outside the scope root (R3)

/D= set install_dir to any path with no containment check, and {install_dir}
substitutes into scheduled_task_create.program and service_install.binary_path
— both of which run as SYSTEM. 'Setup.exe /allusers /D=C:\\Users\\Public\\evil'
therefore created a SYSTEM-level task pointing at a directory any user can
write. Reachable from the example manifest in docs/guides/install-steps.md.

Every install_dir source is now contained to the scope root."
```

---

## Task S2.3: Anchor the four privileged step targets

**Files:**
- Modify: `Steps/ScheduledTaskCreateStep.cs:66-69`, `Steps/ServiceInstallStep.cs:49-62`,
  `Steps/Win32/ComRegisterStep.cs:51`, `Steps/FirewallRuleStep.cs:67-69`
- Modify: `docs/guides/install-steps.md:185-235` (the vulnerable examples)
- Create: `tests/SigilBuild.Wrapper.Tests/Steps/PrivilegedStepContainmentTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
namespace SigilBuild.Wrapper.Tests.Steps;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SigilBuild.Core.Manifest;
using SigilBuild.Wrapper.Engine;
using SigilBuild.Wrapper.Steps;
using SigilBuild.Wrapper.Tests.Helpers;
using Xunit;

public class PrivilegedStepContainmentTests
{
    [Fact]
    public async Task Scheduled_task_refuses_a_program_outside_the_install_dir()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows-only step");

        using var installDir = new TempDir();
        var ctx = new StepContext(
            values: new Dictionary<string, object?>(),
            scope: InstallScope.Machine,
            installDir: installDir.Path,
            appId: "com.example.myapp");

        var step = new ScheduledTaskCreateStep(new InstallStep.ScheduledTaskCreate(
            "t1",
            Name: "MyAppHeartbeat",
            Program: @"C:\Users\Public\evil.exe",
            Arguments: null,
            Trigger: "daily",
            RunLevel: "highest",
            When: null,
            OnFailure: OnFailure.Fail));

        var result = await step.RunAsync(ctx, new RollbackJournal(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("install_dir");
    }
}
```

> Read the actual `InstallStep.ScheduledTaskCreate` record definition in
> `Core/Manifest/` before writing this — match its parameter names and order
> exactly. The shape above follows the catalogue's convention (id first, then
> named fields, `When` and `OnFailure` last) but **verify it**.

- [ ] **Step 2: Run it and watch it fail** — today it proceeds to `schtasks`.

- [ ] **Step 3: Implement the guard in all four steps**

Each resolves its target, then requires **both**
`PathContainment.IsUnderWithoutTraversal(ctx.InstallDir, resolved)` **and**
`StateDirectorySecurity.IsAdminOnlyWritable(resolved)` (from S1 — rebase on its
merge first). Fail the step with a message naming which check failed. The second
check is what actually stops the attack:
a path can be inside `install_dir` and still be user-writable if `install_dir`
itself is (which S2.2 now prevents, but defence in depth is cheap here).

- [ ] **Step 4: Fix the documented examples**

`docs/guides/install-steps.md:193` and `:223` demonstrate
`${parameters.install_dir}\\…` for `service_install` and
`scheduled_task_create`. Keep the shape but add an explicit warning block
stating that the target must resolve inside `install_dir` and that machine-scope
installs refuse user-writable locations.

- [ ] **Step 5: Add cases for the other three steps, verify, commit**

```bash
dotnet test Sigil.slnx -c Release
git add -A && git commit -m "fix(security): anchor privileged step targets to install_dir (R3, R9)"
```

---

## Task S2.4: Contain every step destination, and fail on unresolved tokens

**Files:**
- Modify: `Steps/ConfigFileEditor.cs:28`, `FileCopyStep.cs:23`, `FileDeleteStep.cs:30`,
  `DirectoryDeleteStep.cs:33`, `HttpDownloadStep.cs:33`
- Modify: `Engine/StepContext.cs:399-440`

- [ ] **Step 1: Write the failing tests** covering, for `ini_write` / `json_edit`
      / `xml_edit`: an absolute path outside `install_dir`; a `..` escape; a
      junction in the chain. Plus a case where a path field contains an
      unresolved `{var.x}` — today `StepContext.cs:478` leaves an unknown brace
      token **literal**, so a typo silently creates a directory named
      `{var.x}`.

- [ ] **Step 2: Run and watch them fail.**

- [ ] **Step 3: Implement.** Route every destination through
      `PathContainment.IsUnderWithoutTraversal` against `ctx.InstallDir`, with a
      documented per-step manifest opt-out for deliberate out-of-tree writes
      (some installers legitimately write to `%ProgramData%`). Note
      `FileCopyStep.cs:23` calls `ctx.Resolve`, not `ctx.ResolvePath`, so it
      bypasses even the payload guard — fix that too. Make an unresolved brace
      token in a **path** field fail the step.

- [ ] **Step 4: Run the full suite, expect fixture fallout, use the test-only
      escape hatch from S2.2 rather than weakening the rule. Commit.**

---

## Task S2.5: `/TR` quoting and INI line injection

**Files:**
- Modify: `Steps/ScheduledTaskCreateStep.cs:110-112`, `Steps/IniWriteStep.cs:81,94,105`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Task_program_containing_a_quote_is_refused()
    {
        var act = () => ScheduledTaskCreateStep.BuildCreateArgs(
            name: "T", program: @"C:\a\b"" && calc.exe && """, arguments: null,
            trigger: "daily", runLevel: "limited");

        act.Should().Throw<ArgumentException>().WithMessage("*quote*");
    }

    [Fact]
    public void Ini_value_containing_a_newline_cannot_inject_a_section()
    {
        var act = () => IniEditor.Set("[app]\nx=1\n", "app", "x", "9\n[admin]\nenabled=true");

        act.Should().Throw<ArgumentException>();
    }
```

- [ ] **Step 2: Run, watch fail** — `BuildCreateArgs` currently interpolates
      `$"\"{program}\""` with no escaping, and `IniEditor.Set` concatenates
      `key + "=" + value` verbatim.

- [ ] **Step 3: Implement** — reject `"` in `program`; reject `\r`, `\n`, and a
      leading `[` in INI section/key/value. Rejection beats escaping here: both
      are pack-time-authored values, so a hard failure surfaces the mistake to
      the publisher instead of silently mangling it.

- [ ] **Step 4: Verify, commit.**

---

## Lane S2 definition of done

- [ ] Build clean, suite green, format clean
- [ ] Each negative test confirmed failing on the parent commit
- [ ] The existing `payload://` traversal and zip-slip tests pass **unchanged**
- [ ] Manual: `Setup.exe /allusers /D=C:\Users\Public\evil` is rejected
- [ ] PR into `release/v0.1.0-alpha`

---

# Lane S3 — staged execution

**Findings:** R4 (per-user native cache trusted while elevated), R5 (web stub
predictable-path TOCTOU), R10 (no download size cap), R11 (nothing
Authenticode-checked before elevated execution), R12 (prereq/update verify→launch
gap), R17 (revocation disabled).

**Read first:** rows R4, R5, R10, R11, R12, R17;
`Engine/NativeRuntimeBootstrap.cs:80-110`, `:165-175`;
`Engine/SigilDownloader.cs:107-171`; `Engine/PrerequisiteRunner.cs:100-130`,
`:225-250`; `Engine/AuthenticodeVerifier.cs`; `Update/UpdateRunner.cs:160-220`;
`Update/UpdateSeams.cs:60-90`;
`Packaging/ExeWrapper/ExeWrapperPackager.cs:225-255`;
`Installer.Host/Program.cs:60-140`.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `Engine/SecureStaging.cs` | Create | create a private admin-only staging dir; verify-and-hold a file across launch |
| `Engine/NativeRuntimeBootstrap.cs` | Modify | admin-only cache when elevated; per-file verification |
| `Engine/SigilDownloader.cs` | Modify | `maxBytes` ceiling |
| `Engine/PrerequisiteRunner.cs`, `Update/UpdateRunner.cs` | Modify | stage via `SecureStaging`; Authenticode before launch |
| `Update/UpdateSeams.cs` | Modify | cap the pre-auth manifest buffer |
| `Packaging/ExeWrapper/ExeWrapperPackager.cs` | Modify | randomized staging for the web stub |
| `Engine/AuthenticodeVerifier.cs` | Modify | whole-chain revocation, distinct unknown state |

---

## Task S3.1: `SecureStaging` — the shared primitive

**Interfaces:**
- Produces:
  ```csharp
  namespace SigilBuild.Wrapper.Engine;

  internal sealed class SecureStaging : IDisposable
  {
      /// Creates a freshly-named private directory (GUID) under an admin-only
      /// root when elevated, or %TEMP% when not, with a non-inherited DACL.
      public static SecureStaging Create(string purpose);

      public string Directory { get; }

      /// Opens the staged file with FileShare.Read (denying write and delete),
      /// re-verifies its SHA-256, and returns a handle the caller holds across
      /// Process.Start. Throws if the hash no longer matches — that is the
      /// TOCTOU being closed.
      public FileStream OpenVerified(string fileName, string expectedSha256);

      public void Dispose();
  }
  ```

> **Cross-lane:** `StateDirectorySecurity.IsAdminOnlyWritable` comes from **S1**
> (Task S1.1), which merges first at G1. Rebase on it; do not write a second ACL
> check.

- [ ] **Step 1: Write the failing test** — assert (a) the created directory is
      admin-only per `StateDirectorySecurity.IsAdminOnlyWritable` when elevated,
      (b) `OpenVerified` throws when the file's bytes changed after staging,
      (c) the returned handle prevents another process from replacing the file.

  Case (b) is the one that matters. Write it by staging a file, computing its
  hash, overwriting the file, then calling `OpenVerified` with the original hash
  and asserting it throws.

- [ ] **Step 2: Run, watch fail.** **Step 3: Implement.**
      `FileShare.Read` (not `FileShare.None`, which would break legitimate
      readers, and not `FileShare.ReadWrite`, which defeats the purpose) plus
      re-hashing from the open handle rather than the path — re-hashing by path
      reintroduces the race.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

- [ ] **Step 6: Apply it to the prerequisite and update paths — this is R12.**

  `PrerequisiteRunner.cs:237` stages to
  `Path.Combine(Path.GetTempPath(), $"sigil-prereq-{Guid.NewGuid():N}.exe")` and
  `UpdateRunner.cs:179-181` has the identical shape: the GUID name blocks
  pre-planting, but the file is created with default ACLs and **no handle is
  held** between `AcquireAsync` returning (`:111`) and the launch (`:122`). An
  attacker watching the directory can still win the race.

  Replace both with `SecureStaging.Create(...)` + `OpenVerified(...)`, holding
  the handle across `Process.Start`. Write a test that overwrites the staged
  file between verification and launch and asserts the launch is refused.

- [ ] **Step 7: Run, watch pass. Commit** — one commit for the primitive
      (Steps 1–5), one for its application (Step 6), so review can separate
      "is the primitive right" from "is it used everywhere".

## Task S3.2: The native runtime cache (R4)

- [ ] **Step 1: Write the failing test** — pre-create the content-keyed cache
      directory containing a bogus DLL plus a valid `.sigil-runtime-complete`
      marker, then assert `EnsureNativeDependenciesLoadable` does **not** adopt
      it. Today `:98-101` returns early on the marker alone.
- [ ] **Step 2: Run, watch fail. Step 3: Implement** — when elevated, resolve
      the cache under an admin-only root; verify each extracted file's hash
      against the embedded archive before `AddDllDirectory`; treat the marker as
      a fast path only *after* the directory passes the trust check. Keep the
      call **after** the elevation branch at `Program.cs:71-77` — that ordering
      is correct.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Task S3.3: The web-installer stub (R5)

- [ ] **Step 1: Write the failing test** — assert the generated stub blob's
      `http_download` destination is **not** a pack-time constant. Today
      `ExeWrapperPackager.cs:230` emits `"{temp_dir}/" + fullPackageFileName`.
- [ ] **Step 2: Run, watch fail. Step 3: Implement** — emit a randomized
      per-run staging path and make the stub re-verify the SHA-256 immediately
      before the `run_program` step. Two independent steps with a gap between
      them is the bug; closing it means the verification and the launch must
      share a handle or be re-done adjacently.
- [ ] **Step 4: Run, watch pass.** The true end-to-end proof needs a real
      `Setup.exe`, which **cannot be built on this machine**. Write the test,
      mark it CI-only, and say so in your summary. **Step 5: Commit.**

## Task S3.4: Size ceilings (R10)

- [ ] **Step 1: Write the failing tests** — a server declaring an oversized
      `Content-Length` is refused before the body is read; a server that lies
      and streams more than `maxBytes` is aborted mid-stream. Both matter: the
      first is cheap, the second is the real defence.
- [ ] **Step 2: Run, watch fail. Step 3: Implement** — add `maxBytes` to
      `DownloadVerifiedAsync`; cap `UpdateSeams.FetchAsync` at a few hundred KB.
      That buffer is **pre-authentication** — it is filled before
      `ChannelManifestVerifier.Verify` runs — so it is the higher-value half.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Task S3.5: Authenticode before every elevated launch (R11), and revocation (R17)

- [ ] **Step 1: Write the failing tests** — an unsigned binary is refused at
      launch unless explicitly opted out; a revoked-certificate fixture produces
      **no** trust line.
- [ ] **Step 2: Run, watch fail. Step 3: Implement** — call
      `AuthenticodeVerifier.VerifyFile` immediately before launching any
      downloaded binary (prerequisite, update package, web-stub payload), fail
      closed, with a documented per-prerequisite opt-out for unsigned
      redistributables. Change `fdwRevocationChecks` from `WTD_REVOKE_NONE`
      (`AuthenticodeVerifier.cs:31,95`) to whole-chain, and render a **distinct**
      state when revocation status is unavailable — offline must not silently
      read as trusted, nor as forged.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Lane S3 definition of done

- [ ] Build clean, suite green, format clean
- [ ] Each negative test confirmed failing on the parent commit
- [ ] CI-only tests clearly marked, with an explicit statement of what you could
      not run locally
- [ ] `InstallSession.cs` **not** modified — if the update temp path must move,
      report it to the orchestrator for S1
- [ ] PR into `release/v0.1.0-alpha`

---

# Lane T1 — test truth

**Findings:** R6 (soft-skips report as passed), R21 (coverage gate is
line-only/project-wide-only and three assemblies are missing), R22 (two VM jobs
can pass vacuously).

**Read first:** rows R6, R21, R22; `tests/SigilBuild.Wrapper.IntegrationTests/TestEnvironment.cs`
and the eight gated classes; `tests/SigilBuild.Packaging.Tests/ExeWrapper/{ExeWrapperPackagerTests,ExeWrapperWebInstallerPackTests}.cs`;
`.github/workflows/ci.yml:20-105`, `:106-230`; `wrapper-vm-tests.yml:231-236`
(the one job that already gets this right).

**This lane writes no product code.** It changes how truthfully the existing
tests report.

## Task T1.1: Make every skip a real skip

- [ ] **Step 1: Inventory** —
      `grep -rn "soft-skip\|SKIP:" tests/ --include=*.cs` and list every site.
      Expect ~14 VM-gated facts plus ~10 runtime-gated packaging facts.
- [ ] **Step 2: Convert** — replace each `if (!ShouldRun()) { return; }` with
      `Assert.Skip(reason)` (or a `[VmFact]` attribute computing `Skip`). The
      reason string must name the **missing precondition** —
      `SIGIL_VM_TESTS`, staged runtime, admin — so a reader can act on it.
      Same for the `Console.WriteLine("SKIP:") + return` pattern, which never
      reaches the trx summary at all.
- [ ] **Step 3: Run and record**

```bash
dotnet test Sigil.slnx -c Release 2>&1 | grep -E "^(Passed|Failed)!"
```

Expected: total unchanged at 1097, **passed drops by ~24**, **skipped rises to
~25**. Record the exact numbers — they go in gate G1.

- [ ] **Step 4: Confirm the legitimate skip is untouched** —
      `ComRegisterInstallTests.Live_register_then_unregister_a_real_self_registering_dll`
      is the only `[Fact(Skip=)]` in the repo and its justification is sound.
      Leave it exactly as is.
- [ ] **Step 5: Commit.**

## Task T1.2: Stage the runtime before `dotnet test` in CI

- [ ] **Step 1:** Read `ci.yml:41-42` (the `test` step in the `build` job) and
      `:111,114,181-188` (where `publish-installer-runtime.ps1` runs, in the
      *later, separate* `aot-publish` job).
- [ ] **Step 2:** Add a runtime-staging step to the `build` job **before**
      `dotnet test`, so the pack→`Setup.exe` path actually executes per push.
- [ ] **Step 3:** Push and confirm in the CI log that the previously-skipping
      packaging tests now **run and pass** — not skip. If they fail, that is a
      real finding: file a register row, do not paper over it.
- [ ] **Step 4: Commit.**

## Task T1.3: Per-assembly coverage floors and an assembly allowlist

- [ ] **Step 1:** Read the Python heredoc at `ci.yml:44-105`.
- [ ] **Step 2:** Add an enforced per-assembly floor map, and an
      expected-assembly allowlist that **fails** when an assembly is missing
      from the reports. Today `SigilBuild.Cli`, `SigilBuild.Wrapper`, and
      `SigilBuild.Installer.Host` contribute zero lines and nothing notices,
      because `:86-88` errors only when *no* package is found.
- [ ] **Step 3: Set each floor at the current measured value rounded DOWN, not
      at the aspirational target.** The point is a ratchet, not a cliff.
      Measured locally: project-wide 75.17 %, Core 63.89 %, Signing 68.79 %.
      Re-measure in CI — the local figure comes from a report tree that may hold
      multiple generations. Print target-vs-actual so the gap stays visible.
- [ ] **Step 4:** If the floors would fail CI as written, **report the numbers
      rather than lowering them silently.**
- [ ] **Step 5: Commit.**

## Task T1.4: Stop the VM jobs passing vacuously

- [ ] **Step 1:** Read `wrapper-vm-tests.yml:231-236` — the p11 job's pre-flight
      guard, whose comment names the exact risk: *"an unelevated runner would
      make this whole job go green having exercised nothing, silently."*
- [ ] **Step 2:** Copy that guard into the scope-matrix job (`:35-96`) and the
      p12 job (`:125-171`): assert `SIGIL_VM_TESTS=1` **and** assert the staged
      `runtimes/win-x64/SigilBuild.Installer.Host.exe` exists, before
      `dotnet test`.
- [ ] **Step 3:** Verify by temporarily unsetting `SIGIL_VM_TESTS` in a scratch
      branch and confirming the job **fails** rather than going green. Revert
      the scratch change.
- [ ] **Step 4: Commit.**

## Lane T1 definition of done

- [ ] `dotnet test -c Release` reports a **non-zero skip count**; the exact
      totals are in the PR body
- [ ] `grep -rn "soft-skip" tests/` and `grep -rn '"SKIP:' tests/` return nothing
- [ ] The one legitimate `[Fact(Skip=)]` is untouched
- [ ] CI `build` job executes the packaging tests (green, not skipped)
- [ ] A VM job was observed **failing** with `SIGIL_VM_TESTS` unset
- [ ] No existing threshold lowered
- [ ] PR into `release/v0.1.0-alpha`

**Out of scope:** writing new product tests (the security lanes do that);
changing what any gated test asserts.

# G2 Release Preparation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring every Stage 2/3 lane to done, prove the seven-lane merge chain integrates cleanly, open all lane PRs in merge order, and hand the user a runbook for the G2/G3 gates — everything that can be prepared without a human merge, prepared at once.

**Architecture:** Three independent work fronts (finish lane REL, close R18 on lane S5 + rebase S7, and a local merge-chain pre-flight), converging on one PR-opening pass and a gate runbook. All lane work happens in the existing worktrees (`C:\projects\Sigil-wt-*`); the RC branch `release/v0.1.0-alpha` is never committed to directly (it is PR-protected).

**Tech Stack:** .NET 10 / Native AOT, xUnit + FluentAssertions, GitHub Actions on `windows-latest`, Windows PowerShell 5.1 (no `pwsh` on this box), `gh` CLI.

## Global Constraints

Copied from `AGENTS.md` / `03-RC_ORCHESTRATION.md` — every task implicitly includes these.

- **Native AOT mandatory.** No `Activator.CreateInstance`, `Type.GetType`, `Assembly.Load*`, `DynamicMethod`, unconstrained `MakeGenericType`/`MakeGenericMethod`, expression trees + `.Compile()`, reflection-based `JsonSerializer`. New p/invokes use `[LibraryImport]` like the rest of `SigilBuild.Wrapper.Core`.
- **Verify with `-c Release`, always** — the trim/AOT analyzer only runs on Release. `TreatWarningsAsErrors=true`: a new warning is a broken build.
- **This machine cannot AOT-publish** (`vswhere`/MSVC linker, MSB3073 exit 123). Anything needing a real `Setup.exe` or `dotnet publish -p:PublishAot=true` is CI-only. Say which checks you could not run — never imply a green suite you did not observe.
- **Size budgets:** `sigil.exe` ≤ 15 MB, installer host footprint ≤ 45 MB (gate in `scripts/publish-installer-runtime.ps1`).
- **Conventional Commits** — PR titles are lint-gated (`pr-guards`).
- **Commit attribution:** end commit messages with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>` and PR bodies with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- **The orchestrator cannot merge lane PRs** (ruleset requires 1 approving review, self-approval forbidden, `gh pr merge --admin` must not be used). This plan only *opens* PRs.
- **Nothing is fixed forward on the RC.** A failure in a lane reopens that lane's branch.
- **Lanes never edit `00-GAP_REGISTER.md` or the orchestration doc's progress table** — the orchestrator updates those at gate close (existing pattern: `rc/doc-*` PRs).
- **PowerShell writes corrupt non-ASCII on this box** — write files with the Edit/Write tools, never `Set-Content`/`Out-File`, and verify any generated file containing non-ASCII.
- House test style: xUnit + FluentAssertions, AAA layout; match the `using`-placement style of the file you edit.

**Worktree map (already exists — do not create new worktrees):**

| Lane | Worktree | Branch | State at plan time |
|---|---|---|---|
| REL | `C:\projects\Sigil-wt-rel` | `rc/rel-scaffolding` | 4 commits; REL.6 half-done uncommitted; **not pushed** |
| S5 | `C:\projects\Sigil-wt-s5` | `rc/s5-residual-engine` | 9 commits, pushed; **S5.3/R18 missing** |
| S7 | `C:\projects\Sigil-wt-s7` | `rc/s7-signed-anchorage` | 3 commits **stacked on S5**, pushed |
| S4 / S6 / SUP / DOC | `-wt-s4` / `-wt-s6` / `-wt-sup` / `-wt-doc` | pushed, complete | untouched by this plan |

---

### Task 1: Finish REL.6 — reproducible restore (R23a)

Work in `C:\projects\Sigil-wt-rel` on branch `rc/rel-scaffolding`. The worktree already contains, uncommitted: `RestorePackagesWithLockFile` in `Directory.Build.props`, a finished `NuGet.config`, and one generated lock file (`src/SigilBuild.Core/packages.lock.json`). This task completes the generation, wires CI, verifies from a clean clone, and commits.

**Files:**
- Modify (already dirty): `Directory.Build.props` (keep the existing uncommitted `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` line)
- Add (already present, untracked): `NuGet.config`
- Create: `packages.lock.json` in **every** project directory (src + tests), via restore
- Modify: `.github/workflows/ci.yml:35-36` (the `restore` step)
- No new tests — the verification is the clean-clone locked restore itself

**Interfaces:**
- Produces: committed lock files that Task 5's merge pre-flight and Task 7's runbook reason about. The runbook documents that lane SUP's SkiaSharp bump **will** invalidate these lock files at its rebase (see Task 7).

- [ ] **Step 1: Generate all lock files**

Run (in `C:\projects\Sigil-wt-rel`):
```powershell
dotnet restore Sigil.slnx --force-evaluate
```
Expected: exit 0; a `packages.lock.json` appears next to every `.csproj` (11 src/test projects — verify with `git status --short`, all entries `??` lock files).

- [ ] **Step 2: Wire locked-mode into CI**

In `.github/workflows/ci.yml`, change the `restore` step (currently line 35-36) to:

```yaml
      - name: restore
        # R23a: locked mode fails the build if any package resolves outside the
        # committed packages.lock.json set — with NuGet.config pinning nuget.org
        # as the only feed, this is what makes the restore reproducible.
        run: dotnet restore Sigil.slnx --locked-mode
```
Append only — do not touch T1's coverage gate or any other step. Note the `aot-publish` job (`ci.yml:230-231`) and `wrapper-vm-tests.yml` run `dotnet publish`/`dotnet build` with implicit restore; leave those alone (implicit restore honors the lock files; only the canonical restore step needs `--locked-mode` per REL.6 Step 4's "append to T1's ci.yml; do not rewrite").

- [ ] **Step 3: Verify from a clean clone**

```bash
rm -rf /c/Users/eugen/AppData/Local/Temp/claude/sigil-clean 2>/dev/null
git clone C:/projects/Sigil-wt-rel /c/Users/eugen/AppData/Local/Temp/claude/sigil-clean
cd /c/Users/eugen/AppData/Local/Temp/claude/sigil-clean
dotnet restore Sigil.slnx --locked-mode && echo "locked restore OK"
```
Expected: `locked restore OK`. (Clone sees only committed files — so run this AFTER Step 5's commit, or stage+stash-test. Simplest: commit first (Step 5), then clone-verify, then fix-forward on the branch if it fails.)

- [ ] **Step 4: Build + test Release in the worktree**

```powershell
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release --no-build
dotnet format Sigil.slnx --verify-no-changes
```
Expected: 0 warnings, 0 failed (20-ish honest skips are normal on this box), format clean.

- [ ] **Step 5: Commit**

```bash
git add Directory.Build.props NuGet.config .github/workflows/ci.yml "**/packages.lock.json"
git commit -m "chore(build): lock the package graph and restore in locked mode (R23a)

One feed (nuget.org, declared in NuGet.config with source mapping), a
committed packages.lock.json per project, and --locked-mode in CI: a
restore now either reproduces the audited graph or fails.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
Then run Step 3's clean-clone check against the committed state.

---

### Task 2: REL.5 — `release.yml` (R7) and the CI artifact fix

Work in `C:\projects\Sigil-wt-rel`. Two deliverables: a tag-triggered signed-release workflow, and fixing the trap where `ci.yml` uploads `sigil.exe` without its sibling native DLLs (`libSkiaSharp.dll` ~11 MB, `libsodium.dll` ~0.3 MB — a downloaded bare exe throws `DllNotFoundException`).

**Files:**
- Create: `.github/workflows/release.yml`
- Modify: `.github/workflows/ci.yml:327-332` (the `upload binary` step)
- Modify: `.github/workflows/wrapper-vm-tests.yml:28-29` (add `workflow_call` trigger)

**Interfaces:**
- Consumes: `scripts/publish-installer-runtime.ps1` (exists; `-Rids`, `-Configuration`, `-DestinationRoot`, `-SizeGateMb` parameters, as used in `ci.yml:289-296`); `Directory.Build.props` `<Version>` as the single version source (Task 1's branch already has REL.4's version-agnostic smoke check).
- Produces: workflow file names and secret names the Task 7 runbook instructs the user to provision: secrets `AZURE_TENANT_ID`, `AZURE_CLIENT_ID`, `AZURE_CLIENT_SECRET`, `TRUSTED_SIGNING_ENDPOINT`, `TRUSTED_SIGNING_ACCOUNT`, `TRUSTED_SIGNING_PROFILE`.

- [ ] **Step 1: Make `wrapper-vm-tests.yml` callable**

Change its trigger block (currently `on:\n  workflow_dispatch:`) to:

```yaml
on:
  workflow_dispatch:
  workflow_call:
```
Nothing else in the file changes.

- [ ] **Step 2: Fix the `ci.yml` artifact contents**

Replace the `upload binary` step (`ci.yml:327-332`) with:

```yaml
      # REL.5/R7: the AOT output is NOT single-file — sigil.exe needs its
      # sibling libSkiaSharp.dll and libsodium.dll (logo resizing, ZIP manifest
      # signing). Uploading the bare exe hands users a DllNotFoundException.
      - name: upload binary (full publish directory)
        uses: actions/upload-artifact@v4
        with:
          name: sigil-win-x64
          path: |
            publish/win-x64/*
            !publish/win-x64/*.pdb
          if-no-files-found: error
```

- [ ] **Step 3: Write `.github/workflows/release.yml`**

```yaml
name: release

# REL.5 (R7): tag-triggered signed release. Runs the full VM matrix first —
# a release whose install/uninstall matrix did not run is not a release.
on:
  push:
    tags: ['v*']

permissions:
  contents: write

jobs:
  vm-tests:
    uses: ./.github/workflows/wrapper-vm-tests.yml

  publish:
    runs-on: windows-latest
    needs: vm-tests
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          global-json-file: global.json

      - name: restore (locked)
        run: dotnet restore Sigil.slnx --locked-mode

      - name: require signing secrets
        shell: pwsh
        env:
          HAS_SIGNING: ${{ secrets.AZURE_TENANT_ID != '' && secrets.TRUSTED_SIGNING_ACCOUNT != '' }}
        run: |
          if ($env:HAS_SIGNING -ne 'true') {
            Write-Error "release refused: Azure Trusted Signing secrets are not configured. An unsigned release is not a release — see docs/plan/release/10-G2_G3_RUNBOOK.md for the six secrets to provision."
            exit 1
          }

      - name: aot publish CLI (win-x64)
        run: dotnet publish src/SigilBuild.Cli -c Release -r win-x64 -p:PublishAot=true -o publish/win-x64

      - name: aot publish CLI (win-arm64)
        run: dotnet publish src/SigilBuild.Cli -c Release -r win-arm64 -p:PublishAot=true -o publish/win-arm64

      - name: size gate (win-x64)
        shell: pwsh
        run: |
          $size = (Get-Item publish/win-x64/sigil.exe).Length
          $sizeMb = [math]::Round($size / 1MB, 2)
          Write-Host "sigil.exe (win-x64): $sizeMb MB"
          if ($size -gt 15MB) { throw "sigil.exe is $sizeMb MB, exceeds the 15 MB budget" }

      - name: tag matches Directory.Build.props version
        shell: pwsh
        run: |
          $expected = "v" + (Select-Xml -Path "Directory.Build.props" -XPath "//Version").Node.InnerText.Trim()
          $tag = "${{ github.ref_name }}"
          if ($tag -ne $expected) { throw "tag '$tag' does not match Directory.Build.props version '$expected' (R24: one source of truth)" }

      - name: sign binaries (Azure Trusted Signing)
        uses: azure/trusted-signing-action@v0.5.1
        with:
          azure-tenant-id: ${{ secrets.AZURE_TENANT_ID }}
          azure-client-id: ${{ secrets.AZURE_CLIENT_ID }}
          azure-client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
          endpoint: ${{ secrets.TRUSTED_SIGNING_ENDPOINT }}
          trusted-signing-account-name: ${{ secrets.TRUSTED_SIGNING_ACCOUNT }}
          certificate-profile-name: ${{ secrets.TRUSTED_SIGNING_PROFILE }}
          files-folder: ${{ github.workspace }}/publish
          files-folder-filter: exe,dll
          files-folder-recurse: true

      - name: package archives
        shell: pwsh
        run: |
          New-Item -ItemType Directory -Force dist | Out-Null
          Get-ChildItem publish/win-x64 -Filter *.pdb | Remove-Item
          Get-ChildItem publish/win-arm64 -Filter *.pdb | Remove-Item
          Copy-Item THIRD-PARTY-NOTICES.md publish/win-x64/
          Copy-Item THIRD-PARTY-NOTICES.md publish/win-arm64/
          Compress-Archive -Path publish/win-x64/* -DestinationPath "dist/sigil-${{ github.ref_name }}-win-x64.zip"
          Compress-Archive -Path publish/win-arm64/* -DestinationPath "dist/sigil-${{ github.ref_name }}-win-arm64.zip"

      - name: SHA256SUMS
        shell: pwsh
        run: |
          Get-ChildItem dist | ForEach-Object {
            "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
          } | Set-Content -Path dist/SHA256SUMS -Encoding ascii

      - name: create GitHub release
        env:
          GH_TOKEN: ${{ github.token }}
        shell: pwsh
        run: |
          gh release create "${{ github.ref_name }}" `
            --title "sigil ${{ github.ref_name }}" `
            --notes-file CHANGELOG.md `
            --prerelease `
            dist/*
```
(Exact-pin `azure/trusted-signing-action` to the newest `v0.x` tag if the marketplace shows something newer than `v0.5.1` at implementation time — check with `gh api repos/azure/trusted-signing-action/tags --jq '.[0].name'` and pin what it returns.)

- [ ] **Step 4: Validate structurally — say what you did NOT verify**

There is no YAML parser on this box (`python3` is the Store alias stub; no `actionlint`). Re-read the three workflow files end-to-end for indentation/quoting. GitHub is the authoritative parser: after Task 6 pushes the branch, run `gh workflow list --all` and confirm `release` registers rather than reporting invalid. The signed tag run itself is CI-only and untested until V1's dry-run — the commit message and PR body must say so.

- [ ] **Step 5: Commit and push the branch**

```bash
git add .github/workflows/release.yml .github/workflows/ci.yml .github/workflows/wrapper-vm-tests.yml
git commit -m "ci(release): tag-triggered signed release, gated on the VM matrix (R7)

release.yml: VM matrix -> AOT publish (win-x64 + win-arm64) -> Azure
Trusted Signing -> SHA256SUMS -> GitHub prerelease with the notices
attached. Refuses loudly when the signing secrets are absent. Also fixes
ci.yml's binary artifact to carry sigil.exe's sibling native DLLs — the
bare exe throws DllNotFoundException.

UNVERIFIED: no YAML parser or AOT toolchain on the dev machine; the
workflow registration and the signed run are CI-only (V1 dry-run, G3).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push -u origin rc/rel-scaffolding
```

---

### Task 3: S5.3 — get secrets off the elevated relaunch command line (R18)

Work in `C:\projects\Sigil-wt-s5` on `rc/s5-residual-engine`. Two channels carry secrets today; only one gets code:

1. **Elevation relaunch** (`src/SigilBuild.Wrapper/Program.cs:47`, `src/SigilBuild.Installer.Host/Program.cs:76` → `Elevation.RelaunchElevatedAndWait(args)`): re-emits `/P<secret>=<value>` verbatim to the elevated child, visible to Sysmon/EDR/WMI process auditing. **Fix: DPAPI-protected handoff file.** (Handle inheritance across `ShellExecuteExW` + `runas` does not work — the stage doc pre-authorizes the DPAPI-file design.)
2. **`run_program` child command lines** (`Steps/RunProgramStep.cs`): resolved secrets in step arguments. **Fix: documentation only** — the stage doc says "document that `run_program` arguments are not a secret channel."

Design constraints: p/invoke `CryptProtectData`/`CryptUnprotectData` from `crypt32` directly via `[LibraryImport]` — do **not** add the `System.Security.Cryptography.ProtectedData` package (new package = churn in Task 1's lock files at rebase, and Wrapper.Core deliberately has zero package dependencies for AOT). Use `CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE`: UAC may elevate as a *different* admin account, which breaks CurrentUser-scope DPAPI; machine scope + a create-time restrictive file ACL + delete-after-read is the compensating control. Escape hatch from the stage doc: **if the design grows beyond this shape, STOP and report rather than improvising.**

**Files:**
- Create: `src/SigilBuild.Wrapper.Core/Engine/ElevationSecretHandoff.cs`
- Modify: `src/SigilBuild.Wrapper.Core/Cli/CommandLineParser.cs` (consume the handoff switch)
- Modify: `src/SigilBuild.Wrapper.Core/Engine/InstallSession.cs` (expose the relaunch-args builder)
- Modify: `src/SigilBuild.Wrapper/Program.cs:47`, `src/SigilBuild.Installer.Host/Program.cs:76`
- Modify: `docs/guides/parameters.md` — the `run_program` caveat (S5 sends DOC-owned file text via the PR description if the file belongs to DOC; `parameters.md` is NOT in the file-ownership table, so S5 may edit it directly)
- Test: `tests/SigilBuild.Wrapper.Tests/Engine/ElevationSecretHandoffTests.cs`

**Interfaces:**
- Consumes: `ParsedCommandLine` (`Cli/CommandLineParser.cs:61`) — has `SecretKeys: IReadOnlyList<string>` (canonical casing, populated from `ParameterType.Secret` schema entries at `:507-539`) and the parsed `/P` parameter dictionary.
- Produces:
  - `internal static class ElevationSecretHandoff` with
    `internal static IReadOnlyList<string> PrepareRelaunchArgs(IReadOnlyList<string> args, ParsedCommandLine parsed)` — returns `args` unchanged when no secret parameter is present; otherwise returns a copy with each `/P<name>=<value>` token whose `<name>` is in `parsed.SecretKeys` removed and one `/SecretHandoff=<path>` token appended, having written the DPAPI envelope to `<path>`.
  - `internal static Dictionary<string, string>? TryConsumeHandoff(string path)` — decrypts, deletes the file, returns name→value pairs; returns null (and logs nothing secret) on failure.
  - `InstallSession` (or the session-owning type) exposes `internal IReadOnlyList<string> BuildElevationRelaunchArgs(IReadOnlyList<string> originalArgs)` delegating to the above with its own `ParsedCommandLine`.
  - `CommandLineParser` treats `/SecretHandoff=<path>` as a reserved switch: consumed before parameter binding, its decrypted pairs merged into the parameter set exactly as if they had arrived as `/P` tokens (secret-typed, so all existing redaction keeps working).
- Envelope format (versioned, binary, no JSON context churn): `int32 count`, then per pair `int32 nameByteLen, utf8 name, int32 valueByteLen, utf8 value`, the whole buffer passed through `CryptProtectData`. File written to `Path.Combine(Path.GetTempPath(), "sigil-elevate-" + Guid.NewGuid().ToString("N") + ".dpapi")` with a DACL granting only the current user and `BUILTIN\Administrators` (use `FileSystemAclSupport` patterns already in the repo — grep `FileSecurity` in `Wrapper.Core` and match).

- [ ] **Step 1: Write the failing tests**

In `tests/SigilBuild.Wrapper.Tests/Engine/ElevationSecretHandoffTests.cs` (match the repo's AAA + FluentAssertions style; check neighboring files for `using` placement):

```csharp
[Fact]
public void PrepareRelaunchArgs_removes_secret_parameter_tokens_from_the_relaunch_vector()
{
    // Arrange — a parse whose schema declares "apikey" secret and "mode" not.
    var args = new[] { "/silent", "/Papikey=hunter2", "/Pmode=full" };
    var parsed = ParseWithSchema(args, secret: new[] { "apikey" }); // helper: build ParsedCommandLine via CommandLineParser with a two-parameter schema

    // Act
    var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);

    // Assert — the secret value appears nowhere in the vector; the plain one survives.
    relaunch.Should().NotContain(a => a.Contains("hunter2"));
    relaunch.Should().Contain("/Pmode=full");
    relaunch.Should().ContainSingle(a => a.StartsWith("/SecretHandoff=", StringComparison.OrdinalIgnoreCase));
}

[Fact]
public void Handoff_roundtrips_and_deletes_the_envelope_file()
{
    var args = new[] { "/Papikey=hunter2" };
    var parsed = ParseWithSchema(args, secret: new[] { "apikey" });

    var relaunch = ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed);
    var path = relaunch.Single(a => a.StartsWith("/SecretHandoff=")).Substring("/SecretHandoff=".Length);

    var recovered = ElevationSecretHandoff.TryConsumeHandoff(path);

    recovered.Should().ContainKey("apikey").WhoseValue.Should().Be("hunter2");
    File.Exists(path).Should().BeFalse("the envelope must not outlive its single read");
}

[Fact]
public void PrepareRelaunchArgs_is_the_identity_when_no_secret_parameter_is_present()
{
    var args = new[] { "/silent", "/Pmode=full" };
    var parsed = ParseWithSchema(args, secret: Array.Empty<string>());

    ElevationSecretHandoff.PrepareRelaunchArgs(args, parsed).Should().Equal(args);
}
```

Plus one parser-level test: a `ParsedCommandLine` built from `/SecretHandoff=<path-to-envelope>` binds the decrypted values as secret parameters (assert via the existing redacted re-render at `CommandLineParser.cs:144-254`: the re-rendered line shows `***`, not the value).

- [ ] **Step 2: Run, watch them fail**

```powershell
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "FullyQualifiedName~ElevationSecretHandoff"
```
Expected: FAIL — `ElevationSecretHandoff` does not exist.

- [ ] **Step 3: Implement**

`ElevationSecretHandoff.cs` core p/invoke shape (LibraryImport, matching `Elevation.cs` conventions):

```csharp
[LibraryImport("crypt32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static partial bool CryptProtectData(
    in DATA_BLOB pDataIn, ushort* szDataDescr, IntPtr pOptionalEntropy,
    IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

[LibraryImport("crypt32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static partial bool CryptUnprotectData(
    in DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
    IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

private const uint CRYPTPROTECT_UI_FORBIDDEN = 0x1;
private const uint CRYPTPROTECT_LOCAL_MACHINE = 0x4;
// DATA_BLOB { uint cbData; IntPtr pbData; } — free pDataOut with LocalFree.
```

Integration points, exactly:
- `src/SigilBuild.Wrapper/Program.cs:47`: `return Elevation.RelaunchElevatedAndWait(session.BuildElevationRelaunchArgs(args));`
- `src/SigilBuild.Installer.Host/Program.cs:76`: same substitution.
- `CommandLineParser`: consume `/SecretHandoff=` before `/P` binding; merged names must respect the same canonical-casing rules as `/P<name>` binding (`CommandLineParser.cs:550` area documents the collision rules — read it first). A handoff file that fails to decrypt or is missing → `UsageException` with a message that names the mechanism but never a value ("elevated relaunch secret handoff could not be read — re-run the installer").
- Parent cleanup: in both `Program.cs` call sites, wrap the relaunch in `try/finally` and best-effort-delete the handoff path if the file still exists (child crashed before consuming).

Doc change (`docs/guides/parameters.md`, secrets section): add one paragraph — secret parameters are kept off the elevated relaunch command line via a DPAPI-protected handoff, but a secret a manifest chooses to interpolate into `run_program.args` necessarily lands on that child's command line; `run_program` arguments are **not** a secret channel.

- [ ] **Step 4: Verify**

```powershell
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release --no-build
dotnet format Sigil.slnx --verify-no-changes
```
Expected: 0 warnings, all green locally (the DPAPI tests run on this box — it is Windows). Then the negative-test ceremony the track demands — confirm the new tests fail on the parent commit:

```bash
git stash --include-untracked && git checkout HEAD~1 -- src/ 2>/dev/null || true
dotnet test tests/SigilBuild.Wrapper.Tests -c Release --filter "FullyQualifiedName~ElevationSecretHandoff"
# expect FAIL (type absent), then restore:
git checkout HEAD -- src/ && git stash pop
```
(Practically: run this AFTER committing Step 5, checking out the pre-commit parent.)

- [ ] **Step 5: Commit and push**

```bash
git add src/ tests/ docs/guides/parameters.md
git commit -m "fix(security): keep secret parameters off the elevated relaunch command line (R18)

The UAC relaunch re-emitted /P<secret>=<value> verbatim, visible to any
process-creation auditing. Secrets now cross the elevation boundary in a
DPAPI-protected (machine scope, UI forbidden), ACL-restricted,
delete-after-read envelope; the relaunch vector carries only
/SecretHandoff=<path>. run_program arguments are documented as not a
secret channel — that half of R18 is a manifest-author contract, not a
code path Sigil can redact.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push origin rc/s5-residual-engine
```

---

### Task 4: Rebase S7 onto the new S5 head

`rc/s7-signed-anchorage` is stacked on S5 (its first 9 commits ARE S5). Task 3 added a commit to S5, so S7 must be rebased or its PR will show a stale base and the merge chain breaks.

**Files:** none created — a rebase in `C:\projects\Sigil-wt-s7`.

**Interfaces:**
- Consumes: Task 3's pushed `rc/s5-residual-engine` head.
- Produces: a force-pushed `rc/s7-signed-anchorage` whose history is exactly S5's new head + S7's own 3 commits (`b7ac843` feat, `cefb454` test, `12e3a62` test-adapt).

- [ ] **Step 1: Rebase**

```bash
cd /c/projects/Sigil-wt-s7
git rebase rc/s5-residual-engine
```
Expected: clean replay of exactly 3 commits (S7 touches `ReplayAnchor`/`UninstallEngine` surfaces; Task 3 touched `Elevation`/parser — disjoint). If a conflict appears, resolve minimally in favor of S5's landed shape (the orchestration doc's rule: "S7 adapts to what S5 landed"); if the conflict is in a file Task 3 created, STOP and report — that means the R18 design collided with anchorage work and needs a human look.

- [ ] **Step 2: Verify the stack**

```bash
git log --oneline rc/s5-residual-engine..rc/s7-signed-anchorage   # exactly 3 commits
```
```powershell
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release --no-build
```
Expected: 3 commits, 0 warnings, green.

- [ ] **Step 3: Force-push (lease-guarded)**

```bash
git push --force-with-lease origin rc/s7-signed-anchorage
```

---

### Task 5: Merge-chain pre-flight — prove the seven lanes integrate

No lane has ever been through CI, and the ruleset's strict status checks make the real merge a serial human chain — a conflict discovered at link 5 costs a day. Simulate the whole chain locally first, in the prescribed order, and run the full Release suite on the integrated tree.

**Files:** a throwaway local branch `scratch/g2-preflight` in `C:\projects\Sigil` — never pushed, deleted at the end.

**Interfaces:**
- Consumes: all seven lane branches at their Task 1–4 heads.
- Produces: either "chain merges clean, integrated tree green" recorded for the PR bodies and runbook, or a conflict/failure report naming the pair of lanes — which STOPS this plan at Task 6 for the affected lanes.

- [ ] **Step 1: Build the chain**

```bash
cd /c/projects/Sigil
git checkout -b scratch/g2-preflight release/v0.1.0-alpha
for b in rc/s4-network-update rc/s5-residual-engine rc/s6-step-hardening rc/s7-signed-anchorage rc/rel-scaffolding rc/sup-supply-chain rc/doc-truth; do
  git merge --no-ff --no-edit "$b" || { echo "CONFLICT merging $b"; break; }
done
```
Expected: seven clean merges. Known hazard to expect and handle: **SUP's SkiaSharp 3.119.4 bump conflicts with / invalidates REL's `packages.lock.json`** — if the SUP merge conflicts on lock files or the subsequent locked restore fails, resolve by regenerating (`dotnet restore Sigil.slnx --force-evaluate`) and committing the refreshed lock files as part of the merge; record that the SAME regeneration must happen on the real SUP rebase (already a runbook item, Task 7).

- [ ] **Step 2: Verify the integrated tree**

```powershell
dotnet restore Sigil.slnx --locked-mode
dotnet build Sigil.slnx -c Release
dotnet test Sigil.slnx -c Release --no-build
dotnet format Sigil.slnx --verify-no-changes
```
Expected: locked restore OK, 0 warnings, 0 failed, format clean. Record the exact totals (tests/passed/skipped) — the PR bodies and runbook cite them. AOT publish remains unverifiable on this box; say so wherever the numbers are cited.

- [ ] **Step 3: Tear down**

```bash
git checkout release/v0.1.0-alpha
git branch -D scratch/g2-preflight
```

---

### Task 6: Open the seven lane PRs, in merge order

All PRs target `release/v0.1.0-alpha`. Titles are conventional-commit lint-gated. Opening all seven at once is correct — strict status checks only bite at merge time (each merge invalidates the others' check runs; the runbook tells the user to rebase-and-wait between merges). **The orchestrator does not merge any of them.**

**Files:** none — `gh pr create` × 7.

**Interfaces:**
- Consumes: Task 5's green integrated-tree numbers; each lane's register rows from `03-RC_ORCHESTRATION.md`'s lane→finding map.
- Produces: seven open PR numbers, recorded in the Task 7 runbook.

- [ ] **Step 1: Create the PRs** (run each from the lane's worktree, or pass `--head`)

```bash
gh pr create --base release/v0.1.0-alpha --head rc/s4-network-update \
  --title "fix(security): network trust, channel-manifest freshness, and the download policy (R8, R13, R14, R30, R37, R39, R45, R46)" \
  --body "$(cat <<'EOF'
Lane S4 (Stage 2). Closes R8, R13, R14, R30, R37, R39, R45, R46; records
R47 and R49 as stated limitations in ADR-011.

- Pack-time refusal of http:// in source.url and updates.manifestUrl
- Channel-manifest freshness + replay protection
- Declared downloaded-binary signature policy
- Schema lockstep: schema + manifest-reference + examples + fixtures in one commit

Verified locally (Release build 0 warnings, suite green) and on the
integrated seven-lane pre-flight tree. AOT publish is CI-only for this
machine — the aot-publish check on this PR is its first real run.

Merge order: this PR merges FIRST (S4 → S5 → S6 → S7 → REL → SUP → DOC).

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Repeat with per-lane titles/bodies (same body skeleton: rows closed, bullet summary from the lane's commits, local-verification statement, position in the merge order):

- `rc/s5-residual-engine` — `fix(security): residual engine hardening — honest uninstall, secret handoff, fail-closed guards (R15, R18, R28, R29, R34, R38, R48, R53, R56, R57)`
- `rc/s6-step-hardening` — `fix(steps): step hardening and scope-root containment (R33, R35, R36, R50, R52, R54)` — body notes ADR-012.
- `rc/s7-signed-anchorage` — `fix(security): resolve replay anchoring from the signed blob (R44, R51)` — body notes it is stacked on S5 and must merge after it.
- `rc/rel-scaffolding` — `chore(release): release scaffolding — signed release workflow, notices, changelog, one version literal, locked restore (R7, R23, R23a, R24)` — body states the release.yml run is UNVERIFIED until V1's dry-run and lists the six signing secrets to provision.
- `rc/sup-supply-chain` — `chore(deps): supply-chain floor — Dependabot, vulnerability gate, SkiaSharp off preview (R42)` — body carries the lock-file regeneration warning for its own rebase.
- `rc/doc-truth` — `docs: make the docs tell the truth about the product (R25, R26, R26a, R27, R41a, R43, R55)`

- [ ] **Step 2: Confirm checks start**

```bash
gh pr list --state open
gh workflow list --all   # 'release' must now be registered (REL branch pushed) — REL.5 Step 3's authoritative parse check
```
Expected: seven open PRs, each with the six required checks pending/running; `release` workflow listed. If any check goes red, that lane reopens (fix in its worktree, push) — do not fix forward anywhere else.

---

### Task 7: The G2/G3 runbook

Everything left after this plan needs a human hand (merges, repo settings, secrets, VM dispatch). Write it down as one document, committed via an eighth, docs-only PR.

**Files:**
- Create: `docs/plan/release/10-G2_G3_RUNBOOK.md` on new branch `rc/doc-g2-runbook` (cut from `release/v0.1.0-alpha`)

**Interfaces:**
- Consumes: Task 6's PR numbers; Task 5's integrated-tree numbers; Task 2's secret names.
- Produces: the document the user executes. Contents, in full:

- [ ] **Step 1: Write the runbook** with these sections (real PR numbers substituted):

```markdown
# G2/G3 runbook — what only a human can do from here

## 1. Merge chain (strict checks force this to be serial)
Order: S4 #__ → S5 #__ → S6 #__ → S7 #__ → REL #__ → SUP #__ → DOC #__ → runbook #__.
For each link: approve+merge → the remaining PRs' checks are invalidated →
the orchestrator rebases the next branch onto the new RC head and pushes →
wait for green → merge. Two known rebase traps:
- S7 rides S5: after S5 merges, S7 rebases onto the RC (its first 10
  commits vanish into the base — expected; 3 commits remain).
- SUP after REL: SkiaSharp 3.119.4 invalidates REL's packages.lock.json.
  On SUP's rebase run `dotnet restore Sigil.slnx --force-evaluate` and
  commit the refreshed lock files, or its locked-mode CI restore fails.

## 2. Repo settings (owner-only, before G2 closes)
- Settings → Security → enable Private vulnerability reporting (R23; currently OFF, verified 2026-09-08).
- Provision Azure Trusted Signing and add the six actions secrets:
  AZURE_TENANT_ID, AZURE_CLIENT_ID, AZURE_CLIENT_SECRET,
  TRUSTED_SIGNING_ENDPOINT, TRUSTED_SIGNING_ACCOUNT, TRUSTED_SIGNING_PROFILE.
  Without them release.yml refuses to run — by design.
- Reserve NuGet IDs SigilBuild and SigilBuild.UpdateSdk (R41a, G4 item — do early).

## 3. G2 manual checks (after DOC merges) — commands inline
[the ten G2 boxes from 03-RC_ORCHESTRATION.md, each with its exact command
 and expected outcome, e.g. `dotnet restore --locked-mode` from a clean
 clone; `sigil init --template full` then pack; a http:// manifest failing
 to pack with the S4 diagnostic; the corrected silent-install line from
 docs/guides/parameters.md against a real Setup.exe — the last one needs
 the CI-built artifact since this machine cannot produce Setup.exe]

## 4. G3 (Stage 4 / V1) prerequisites
- Dispatch wrapper-vm-tests.yml once by hand (it has NEVER run) and then
  add a schedule trigger via a follow-up PR, per the G3 checkbox.
- V1 lane runs per 08-STAGE-4-verification.md: walk the register, re-attack,
  re-measure, release dry-run with a throwaway tag (exercises release.yml
  end to end, including signing), clean-machine install of the artifact.

## 5. What this preparation did NOT verify
- No AOT publish ran on the dev machine (toolchain, known); the seven PRs'
  aot-publish checks are the lanes' first CI contact.
- release.yml has never executed; its first run is the V1 dry-run.
- The integrated-tree numbers cited in the PRs are local (no VM legs).
```

- [ ] **Step 2: Commit, push, open PR #8**

```bash
git checkout -b rc/doc-g2-runbook release/v0.1.0-alpha
# (write the file)
git add docs/plan/release/10-G2_G3_RUNBOOK.md
git commit -m "docs: G2/G3 runbook — the human-only remainder of the release (merge chain, settings, gates)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
git push -u origin rc/doc-g2-runbook
gh pr create --base release/v0.1.0-alpha --title "docs: G2/G3 runbook — the human-only remainder of the release" --body "..."
git checkout release/v0.1.0-alpha
```

- [ ] **Step 3: Final report to the user** — one message: the eight PR links in merge order, the integrated-tree test numbers, the explicit list of unverified surfaces (AOT publish, release.yml, VM matrix), and the first action only they can take (merge S4's PR; flip private vulnerability reporting on; start Trusted Signing provisioning, which has lead time).

---

## Self-review notes

- **Spec coverage:** REL.5 ✓ (Task 2), REL.6 ✓ (Task 1), S5.3/R18 ✓ (Task 3), S7 stack integrity ✓ (Task 4), push branches ✓ (Tasks 2/3/4), seven PRs in merge order ✓ (Task 6), G2/G3 gate materials ✓ (Task 7). Integration risk not in the original ask but load-bearing ✓ (Task 5).
- **Deliberately out of scope:** merging anything (human-only), updating `00-GAP_REGISTER.md` / the orchestration progress table (orchestrator-at-gate pattern, rides the runbook PR or a gate-close docs PR), Stage 4 / V1 itself, and any repo setting.
- **Type consistency:** `ParsedCommandLine.SecretKeys` (verified at `CommandLineParser.cs:102-103`), `ElevationSecretHandoff.PrepareRelaunchArgs` / `TryConsumeHandoff` names used consistently across Task 3's steps; both `Program.cs` call sites verified at `:47` and `:76`.
- **Escape hatches preserved:** Task 3 STOP-and-report if the handoff design grows; Task 4 STOP if the rebase conflicts with Task 3's new files; Task 5 conflict stops Task 6 for affected lanes.

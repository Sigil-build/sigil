# Stage 3 — Release surface

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> Global constraints live in [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints)
> and apply to every task here.

**Goal:** Make it possible for a stranger to obtain, verify, and understand the
software — and make every published claim true.

**Architecture:** Three lanes touching no engine code, which is why this stage
overlaps Stage 2. REL builds the distribution path, SUP builds the dependency
safety net, DOC makes the documentation match the code.

**Runs after:** Gate G1, in parallel with Stage 2. **Merge order at G2:** after
Stage 2's lanes — REL → SUP → DOC.

| Lane | Model | Branch | Findings |
|---|---|---|---|
| `REL` release scaffolding | sonnet-5 | `rc/rel-scaffolding` | R7, R23, R23a, R24 |
| `SUP` supply chain | sonnet-5 | `rc/sup-supply-chain` | R42 |
| `DOC` docs truth | sonnet-5 | `rc/doc-truth` | R25, R26, R26a, R27, R41a, R43 · **+R55** |

**Cross-lane rules:** `ci.yml` belongs to T1 (Stage 1) — REL and SUP **rebase
onto the merged RC and append only**, never rewrite T1's coverage gate.
`docs/guides/install-steps.md` belongs to S2 — DOC must not touch it.
`schemas/sigil-schema.json`, `docs/manifest-reference.md`, and `examples/**`
belong to S4 — DOC must not touch them either. `docs/plan/**` is read-only
history; DOC adds banners, never rewrites content.

---

# Lane REL — release scaffolding

**Read first:** rows R7, R23, R23a, R24; `.github/workflows/ci.yml:106-230`
(as merged by T1); `Directory.Build.props`; `Directory.Packages.props`;
`src/SigilBuild.Cli/Program.cs:11`; `src/SigilBuild.Cli/SigilBuild.Cli.csproj:9`;
`tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36`.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `SECURITY.md` | Create | disclosure channel and policy |
| `CHANGELOG.md` | Create | what 0.1.0 contains |
| `THIRD-PARTY-NOTICES.md` | Create | attribution, including the native payloads |
| `.github/workflows/release.yml` | Create | tag-triggered signed release |
| `Directory.Build.props` | Modify | version single source; `RestorePackagesWithLockFile` |
| `NuGet.config` | Create | declare nuget.org as the only feed |
| `**/packages.lock.json` | Create | committed lock files |
| `src/SigilBuild.Cli/Program.cs`, `SigilBuild.Cli.csproj` | Modify | remove the duplicated literal |
| `tests/SigilBuild.Cli.Tests/VersionCommandTests.cs` | Modify | assert agreement, not a literal |
| `.github/workflows/ci.yml` | Modify | version smoke; fix the artifact contents |

---

## Task REL.1: `SECURITY.md` (R23)

- [ ] **Step 1: Write it.** Required content: a contact address, a supported-versions
      table, and a disclosure window. Model the tone on the existing
      `CODE_OF_CONDUCT.md`, which already uses `conduct@sigil.build` — use a
      parallel `security@sigil.build`.

      State plainly that Sigil produces installers that self-elevate, write
      HKLM, register COM, and open firewall rules, and that privilege-escalation
      reports are in scope. Ask reporters **not** to open a public issue for
      those.

- [ ] **Step 2: Enable GitHub private vulnerability reporting.** This is a repo
      setting, not a file. Note in the PR body that it was done (or that it
      needs an admin to do it).
- [ ] **Step 3: Commit.**

## Task REL.2: `THIRD-PARTY-NOTICES.md` (R23) — a compliance defect, not a courtesy

- [ ] **Step 1: Enumerate what actually ships.** Read
      `Directory.Packages.props` and separate runtime from test-only and
      build-only (`PrivateAssets="all"`).

- [ ] **Step 2: Name the native payloads explicitly.** This is the part that
      makes it compliance rather than politeness — every *NuGet package* is
      permissive, but the **native binaries redistributed beside `sigil.exe`
      are not all MIT**:

      | Binary | Bundles | Licence |
      |---|---|---|
      | `libSkiaSharp.dll` (~11 MB) | Skia | BSD-3-Clause |
      | | ANGLE | BSD-3-Clause |
      | | HarfBuzz | MIT |
      | `libsodium.dll` (~0.33 MB, via NSec.Cryptography) | libsodium | ISC |

      BSD-3-Clause and ISC both carry **binary-redistribution attribution
      requirements** that an MIT-only `LICENSE` does not satisfy.

      **Verify each licence from the package's own metadata before writing it
      down** — read `~/.nuget/packages/<pkg>/<ver>/*.nuspec` and the bundled
      licence files. Do not copy the table above on trust; it is a starting
      point from the audit, and getting a licence wrong in a notices file is
      worse than having no file.

- [ ] **Step 3:** Also cover the managed runtime dependencies: Avalonia (+
      Desktop, Themes.Fluent), Svg.Skia, ZstdSharp.Port, YamlDotNet,
      System.CommandLine, System.Text.Json, Azure.Identity, Polly (+
      Extensions.Http), Microsoft.Extensions.FileSystemGlobbing.
- [ ] **Step 4:** Make the release workflow ship it **beside the binaries**
      (Task REL.5), not only in the repo.
- [ ] **Step 5: Commit.**

## Task REL.3: `CHANGELOG.md` (R23)

- [ ] **Step 1:** Keep-a-Changelog format, one `0.1.0` entry covering T1–T18 and
      P0–P13. Source it from `git log` and the two plan docs — **not** from
      memory.
- [ ] **Step 2:** Add a `### Known limitations` subsection lifted from
      `02-READINESS_REPORT.md`'s draft. It is written to be honest rather than
      flattering; keep it that way.
- [ ] **Step 3: Commit.**

## Task REL.4: One version literal (R24)

**Files:** `Directory.Build.props`, `src/SigilBuild.Cli/Program.cs:11`,
`src/SigilBuild.Cli/SigilBuild.Cli.csproj:9`,
`tests/SigilBuild.Cli.Tests/VersionCommandTests.cs:36`,
`.github/workflows/ci.yml:136`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Reported_version_matches_the_assembly_informational_version()
{
    var expected = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion
        .Split('+')[0];          // strip any source-revision suffix

    var sw = new StringWriter();
    Program.WriteVersion(sw);

    sw.ToString().Trim().Should().Be(expected,
        "the CLI must report the version the build stamped, not a hand-maintained const");
}
```

> Replace `VersionCommandTests.cs:36`'s literal assertion with this. Read the
> existing test for the real entry point — `Program.WriteVersion` is
> illustrative; match what the file actually calls.

- [ ] **Step 2: Run, watch fail** — today `Program.cs:11` is
      `public const string Version = "0.0.1-alpha";`, wholly independent of the
      csproj value at `:9`. Nothing reconciles them.
- [ ] **Step 3: Implement** — move the version to `Directory.Build.props`,
      delete the `const`, read `AssemblyInformationalVersionAttribute` at
      runtime. Change `ci.yml:136`'s smoke from a literal comparison to
      agreement with the csproj-derived value.
- [ ] **Step 4: Prove there are no literals left**

```bash
grep -rn "0\.0\.1-alpha" --include='*.cs' --include='*.csproj' --include='*.yml' --include='*.props' . | grep -v docs/
```

Expected: **no output**.

- [ ] **Step 5: Leave the release version parameterised.** The orchestrator
      picks the number at G3/G4. Do not hardcode `0.1.0-alpha`; leave a clearly
      marked single place to set it.
- [ ] **Step 6: Commit.**

## Task REL.5: `release.yml` (R7)

- [ ] **Step 1: Write the workflow.** Trigger `on: push: tags: ['v*']`. Steps:
      AOT-publish win-x64 **and** win-arm64; Authenticode-sign; generate a
      `SHA256SUMS` file; attach binaries + `SHA256SUMS` +
      `THIRD-PARTY-NOTICES.md` to a GitHub Release. Gate the whole thing on the
      `wrapper-vm-tests` matrix passing.

- [ ] **Step 2: Fix the artifact contents — this is the trap.** `ci.yml:224`
      uploads `path: publish/win-x64/sigil.exe`, a single file. But the AOT
      output is **not** single-file: `publish/win-x64/` also contains
      `libSkiaSharp.dll` (11.09 MB) and `libsodium.dll` (0.33 MB), which
      `sigil.exe` needs for logo resizing and ZIP manifest signing. A user who
      downloads the current artifact gets a binary that throws
      `DllNotFoundException`.

      Upload the whole publish directory minus `.pdb` files, in **both**
      `ci.yml` and `release.yml`.

- [ ] **Step 3: Validate the YAML**

> **There is no local YAML parser on the dev machine** — `python3` resolves to
> the Windows Store alias stub, not an interpreter, and `actionlint` is not
> installed. (This bit Stage 0: two tasks specified a `python3` check that could
> not run.) Validate structurally by reading the file, and treat GitHub as the
> authoritative parser: push the branch and confirm the workflow is picked up
> rather than reported as invalid. Do not claim "YAML valid" from a check you
> did not run — say what you actually did.

```bash
gh workflow list --all | grep -i release || echo "not registered yet — expected until the branch is pushed"
```

- [ ] **Step 4: Do not claim you verified the release.** The signed run is
      tag-triggered and CI-only, and AOT publish fails on this machine. Say so
      explicitly. V1 performs the dry-run at G3.
- [ ] **Step 5: Commit.**

## Task REL.6: Reproducible restore (R23a)

- [ ] **Step 1:** Set `RestorePackagesWithLockFile=true` in
      `Directory.Build.props`.
- [ ] **Step 2:** Restore and commit the generated `packages.lock.json` files.
- [ ] **Step 3:** Create `NuGet.config` declaring nuget.org as the only feed —
      today there is none, so the feed set is inherited from the machine.
- [ ] **Step 4:** Add `--locked-mode` to the CI restore (append to T1's
      `ci.yml`; do not rewrite the coverage gate).
- [ ] **Step 5: Verify from a clean clone**

```bash
git clone . /tmp/sigil-clean && cd /tmp/sigil-clean
dotnet restore Sigil.slnx --locked-mode && echo "locked restore OK"
```

- [ ] **Step 6: Commit.**

## Lane REL definition of done

- [ ] Build clean, suite green, format clean
- [ ] `dotnet restore --locked-mode` succeeds from a clean clone
- [ ] No `0.0.1-alpha` literal outside `docs/`
- [ ] `SECURITY.md`, `CHANGELOG.md`, `THIRD-PARTY-NOTICES.md` present; notices
      name Skia, ANGLE, HarfBuzz, and libsodium, each licence **verified from
      package metadata**
- [ ] `release.yml` and the fixed artifact paths in place, explicitly **unverified
      locally**
- [ ] `ci.yml` changes are additive to T1's version

---

# Lane SUP — supply chain

**Read first:** row R42; `Directory.Packages.props`; `.github/workflows/`.

## Task SUP.1: Dependency update automation

- [ ] **Step 1:** Create `.github/dependabot.yml` covering `nuget` and
      `github-actions`, weekly. Neither exists today
      (`grep -rn "dependabot" .` → nothing).
- [ ] **Step 2: Commit.**

## Task SUP.2: Vulnerability scanning

- [ ] **Step 1:** Add a CI step running
      `dotnet list package --vulnerable --include-transitive`, failing on
      High/Critical. Append to `ci.yml` (rebase on T1 first).
- [ ] **Step 2: Run it locally and record the findings**

```bash
dotnet list package --vulnerable --include-transitive 2>&1 | tee /tmp/vuln.txt
```

The audit deliberately did **not** guess at advisory status for
`Azure.Identity 1.13.1`, `Polly 7.2.4`, `System.Text.Json 9.0.0`,
`NJsonSchema 11.0.2`, or the others — it flagged them UNVERIFIED and recommended
the scan instead. This step is that scan.

- [ ] **Step 3: Do not fix findings in this lane.** File each as a new row in
      `00-GAP_REGISTER.md` and tell the orchestrator. A dependency bump during a
      security track is its own risk and deserves its own review.
- [ ] **Step 4: Commit.**

## Task SUP.3: SBOM

- [ ] **Step 1:** Add CycloneDX SBOM generation to `release.yml` (coordinate
      with REL, which creates that file — REL merges first).
- [ ] **Step 2: Commit.**

## Task SUP.4: The preview-dependency call

- [ ] **Step 1: Establish the constraint.** `SkiaSharp` and
      `SkiaSharp.NativeAssets.*` are pinned at `3.119.4-preview.1.1`
      (`Directory.Packages.props:45-47`), per the inline comment only "to
      satisfy Avalonia 12 transitive requirement" — a **preview native binary
      inside privileged software**. `System.CommandLine` is at
      `2.0.0-beta4.22272.1` (`:35`), the September-2022 build: ~4 years stale
      and unlikely to receive a security fix.

      Check whether a **stable** SkiaSharp satisfies Avalonia 12 now:

```bash
dotnet list package --include-transitive | grep -i skia
```

- [ ] **Step 2: Decide and record.** If a stable version works, move to it and
      run the full suite plus a headed wizard launch. If Avalonia 12 still
      requires the preview, **do not force it** — record the constraint as a
      known limitation for the release notes and hand the text to REL for
      `CHANGELOG.md`.
- [ ] **Step 3:** For `System.CommandLine`, evaluate 2.0 GA and **budget** the
      migration — do not attempt it in this lane. The API changed between betas;
      a CLI-surface rewrite inside a security track is the wrong trade.
- [ ] **Step 4: Commit.**

## Lane SUP definition of done

- [ ] Build clean, suite green, format clean
- [ ] Dependabot and the vulnerability scan are in CI
- [ ] The scan's current findings are recorded as register rows, not silently fixed
- [ ] The SkiaSharp decision is written down either way

---

# Lane DOC — docs truth

**Read first:** rows R25, R26, R26a, R27, R41a, R43. **Code is the authority
for every claim you write:**
`Wrapper.Core/Cli/CommandLineParser.cs` (the flag list at `:372` and `:504`),
`Engine/InstallSurvivability.cs:17`,
`Packaging/ExeWrapper/ExeWrapperPackager.cs:40,:47,:134`,
`Packaging/Zip/ZipPackager.cs:24-25`.

**For every flag and filename you write, cite the code line you took it from in
your PR summary.** The whole finding is that the docs drifted from the code; a
fix authored from memory reproduces it.

## Task DOC.1: The silent-install command that hard-fails (R26) — do this first

Highest user impact in the lane: this is plausibly the most-copied line in the
docs, and it does not work.

- [ ] **Step 1: Confirm the failure**

```bash
grep -n "install_dir=" docs/guides/installer-wizard.md docs/guides/parameters.md
sed -n '495,506p' src/SigilBuild.Wrapper.Core/Cli/CommandLineParser.cs
```

`installer-wizard.md:97` and `parameters.md:78` both document:

```
setup.exe /S /install_dir="C:\Apps\MyApp" /edition=professional
```

Parameter overrides are accepted **only** under a `P` prefix (`:497`). A bare
`/install_dir=` or `/edition=` falls through to `:503` and throws
`UsageException: unrecognized flag`.

- [ ] **Step 2: Fix both command lines and the prose** at
      `installer-wizard.md:97,100`, `parameters.md:78,81`, and
      `packaging-formats.md:39` — all show the wrong `/Name=Value` form.
- [ ] **Step 3: Verify by execution, not by reading.** Copy the corrected line
      and run it. If a real `Setup.exe` cannot be built here (AOT publish fails
      on this machine), run the tokens through `CommandLineParser` in a scratch
      test instead — and **say that is what you did**.
- [ ] **Step 4: Commit.**

## Task DOC.2: A reference page for the fifteen runtime flags (R26)

- [ ] **Step 1: Enumerate from the code.** The authoritative list is the
      parser's own error text at `CommandLineParser.cs:372,504`, plus
      `Wrapper/Program.cs:18` and `Installer.Host/Program.cs:22` for
      `/?`|`/help`:

      `/silent`, `/S`, `/verysilent`, `/Update`, `/Uninstall`, `/allusers`,
      `/currentuser`, `/force-downgrade`, `/closeapps`, `/D=path`,
      `/LOG[=path]`, `/lang=tag`, `/launch`, `/PName=Value`,
      `/Poption.Name=Value`, `/?`|`/help`.

      Four are documented **nowhere** today (`/verysilent`, `/launch`,
      `/Poption.Name=Value`, `/?`). `/D=` — the input to R3 — appears only in
      passing at `upgrades.md:45`.

- [ ] **Step 2: Create `docs/setup-exe-reference.md`** with each token's
      semantics and the exit codes. Link it from `docs/README.md`.
- [ ] **Step 3: Add a comment at the top saying the page is hand-written** and
      why: `scripts/docs/generate-cli-reference.ps1` introspects the `sigil`
      command tree, **not** `CommandLineParser`, so it cannot produce this page
      — and a future maintainer must not "regenerate" it away.
- [ ] **Step 4: Note S2's containment rules** for `/D=` — it now refuses paths
      outside the scope root. Rebase on the merged RC so you document actual
      behaviour.
- [ ] **Step 5: Commit.**

## Task DOC.3: Output filenames (R26)

- [ ] **Step 1: Establish code truth**

```bash
grep -n "UninstallerFileName" src/SigilBuild.Wrapper.Core/Engine/InstallSurvivability.cs
sed -n '38,50p;130,140p' src/SigilBuild.Packaging/ExeWrapper/ExeWrapperPackager.cs
sed -n '20,30p' src/SigilBuild.Packaging/Zip/ZipPackager.cs
```

Truth: `uninstall.exe`; `{App.Name}-{Version}-{arch}-Setup.exe` and
`-WebSetup.exe`; ZIP is a **flat** `{out}/{App.Id}-{Version}-{arch}.zip`.

- [ ] **Step 2: Replace `uninstaller.exe` → `uninstall.exe`** in the six files
      that have it wrong: `docs/guides/uninstaller.md:7,20,32`,
      `docs/README.md:28`, `docs/getting-started.md:174`,
      `docs/guides/installer-wizard.md:54`,
      `docs/guides/packaging-formats.md:36`. Only `upgrades.md:20` is already
      right. Note `uninstaller.md:20` documents the `UninstallString` — anyone
      scripting a silent uninstall from the docs currently points at a
      nonexistent path.
- [ ] **Step 3: Fix `getting-started.md`** — four errors: `:42` calls
      `sigil.exe` "a single-file ~1 MB binary" (actual **13.98 MB** plus two
      sibling native DLLs); `:118` claims ZIP output goes to
      `./dist/<app-id>-<version>/`; `:109-112` says "only the ZIP path is
      functional… MSIX lands in Sprint 4", stale by ~10 phases; `:120,131,174`
      call the installer `setup.exe`.
- [ ] **Step 4: Commit.**

## Task DOC.4: README (R25)

- [ ] **Step 1: Delete the false claims.** `README.md:16-17` promises "zstd
      dictionary-mode delta updates with a built-in client SDK". Both halves are
      false: `docs/architecture/adr-010-delta-update-deferral.md:18-25`
      (Accepted) states delta patches are **explicitly deferred**, and there is
      no Update SDK project in `src/`. Either delete or mark clearly as roadmap.
- [ ] **Step 2: Fix the install section.** `:19-30` advertises
      `winget install`, `curl … | sh`, and `dotnet tool install -g SigilBuild` —
      none exist, and the latter two are offered for **macOS/Linux** on a
      Windows-only product. Coordinate with REL: once `release.yml` ships a
      GitHub Release, point at that. Until then, "build from source" is the only
      true answer.
- [ ] **Step 3: Describe what actually exists.** The README never mentions the
      exe wizard, `Setup.exe`, the install-step engine, the rollback journal, or
      the uninstaller — the single largest body of shipped work. Add a short
      "What you get" section.
- [ ] **Step 4: Commit.**

## Task DOC.5: `architecture-overview.md` (R26a)

Four errors in a live, user-facing doc:

- [ ] **Step 1:** `:90` says "Compression | **ZstdNet** + native fallback" —
      wrong package, and the fallback does not exist.
      `Directory.Packages.props:38-43` pins **ZstdSharp.Port**, pure-managed,
      "nothing to bundle". (The *code* comments are the accurate ones.)
- [ ] **Step 2:** `:91` says "Crypto (Ed25519) | NSec.Cryptography". NSec
      survives only in `Signing/Local/ZipManifestSigner.cs`; the update engine
      signs with **ECDSA P-256 via BCL `ECDsa`**
      (`adr-009-update-manifest-signature.md:257`).
- [ ] **Step 3:** `:70-77` omits `SigilBuild.Signing`,
      `SigilBuild.Wrapper.Core`, and `SigilBuild.Localization.Generator` — four
      of nine projects — and still calls `SigilBuild.Wrapper` the wizard engine,
      which moved to `Wrapper.Core` in T1. Regenerate from `Sigil.slnx`.
- [ ] **Step 4:** `:98` claims the metrics are "enforced by CI, not
      aspirations". Of seven rows CI enforces one (the 15 MB gate,
      `ci.yml:133`) plus coverage. Relabel "targets" and mark which are gated.
      Note "Delta patch generation ≤ 30 s" is a metric for a deferred feature.
- [ ] **Step 5: Commit.**

## Task DOC.6: ADR collision and CODEOWNERS (R27)

- [ ] **Step 1: Confirm the collision** — `docs/architecture/` holds
      `adr-009-update-manifest-signature` and `adr-010-delta-update-deferral`;
      `sigil-docs/architecture/` holds a **different**
      `adr-009-brand-token-runtime-json-vs-source-gen` and
      `adr-010-schema-validator-monolith`.
- [ ] **Step 2: Renumber and move.** The two orphans become **013** and **014**
      (S4 takes 011 for update freshness, S6 takes 012 for COM isolation — check
      the merged RC before assigning, and take the next free numbers if those
      moved). Move them into `docs/architecture/`, then delete `sigil-docs/`.
- [ ] **Step 3: Fix `CODEOWNERS`.** It currently routes
      `/sigil-docs/architecture/` and `/sigil-docs/decisions.md` to
      `@Sigil-build/tech-leads`. The first is the **stale** tree; the second
      **does not exist**. The live `docs/architecture/` — which holds the update
      signature ADR and the new security ADRs — has **no** tech-lead review
      requirement. Repoint at `/docs/architecture/`.
- [ ] **Step 4: Commit.**

## Task DOC.7: Plan-doc banners and the NuGet ID (R43, R41a)

- [ ] **Step 1: Add forward-pointer banners, do not rewrite.** `AGENTS.md`
      declares `docs/plan/*` read-only history. Add a one-line banner at the top
      of `docs/plan/ORCHESTRATION_PLAN.md` and
      `docs/plan/feature-parity/01-IMPLEMENTATION_PLAN.md` pointing at
      `docs/plan/release/00-GAP_REGISTER.md` for current state.

      What is stale, for the banner's wording: `ORCHESTRATION_PLAN.md:6` claims
      "527 tests green" (measured: **1097**) and never mentions the P-track;
      `01-IMPLEMENTATION_PLAN.md:188` still shows P13 as unmerged, though P13 is
      commit `1be494c` — the audited HEAD.

      **Do not edit their content to match the code.** The banner corrects the
      reader without falsifying the record.

- [ ] **Step 2: `docs/sprint-01/identifier-reservation.md` (R41a).** Update it
      to reflect reality or delete it. `:13` marks `SigilBuild` as a "Reserved
      placeholder **to be published**"; `:23` `SigilBuild.UpdateSdk` "Pending
      public reservation" — while `README.md:32` tells users to
      `dotnet tool install -g SigilBuild`.
- [ ] **Step 3: Escalate the squat risk.** Tell the orchestrator explicitly that
      the NuGet IDs are **still unclaimed** while the README advertises them.
      Reserving them is an orchestrator chore at G4 and should happen **before
      the repo gets attention**.
- [ ] **Step 4: Commit.**

## Task DOC.8: the `parameters.install_dir` idiom, and two false claims (R55)

**Added 2026-08-11**, after lane S2 found this in Stage 1 and fixed only the files
it owned. Read row R55 in `00-GAP_REGISTER.md`.

**The idiom itself.** Declaring a manifest parameter *named* `install_dir` creates
a **second, unrelated value** that diverges from the real one the moment a user
installs anywhere but the default. The correct idiom is **`{install_dir}`** — the
single value that the default, `installer.install_dir:`, the wizard's Destination
screen, `/D=`, upgrade-in-place and S2's containment guards all agree on. S2
converted 13 snippets in the files it owned and rewrote both shipped
`examples/exe-wrapper/**` manifests, which had been **aborting at their first
`file_copy`** — CI stayed green because the example gate is schema-only.

- [ ] **Step 1: Fix the five surviving files** — `docs/guides/parameters.md:63`,
      `docs/guides/uninstaller.md:61`, `docs/migration/from-inno.md:21`,
      `docs/migration/from-wix.md:65`, `docs/migration/from-nsis.md:55`.
      **Re-derive the line numbers**; they are from Stage 1 and DOC.1/DOC.3 edit
      two of these files ahead of you.

- [ ] **Step 2: Delete a claim that is simply false.** `from-wix.md` and
      `from-nsis.md` both assert that a destination screen is *"auto-inserted when
      `parameters.install_dir` is declared"*. It is not — `InstallerViewModel.cs`
      adds it **unconditionally**. Verify against the code before rewriting, per
      this lane's standing rule, and cite the line in your PR summary.
      **Migration guides are the first thing a switching publisher reads**, which
      is why a false mechanism here costs more than the same sentence elsewhere.

- [ ] **Step 3: A release note, not a doc fix.** S2's `directory_create`
      containment required the `allow_outside_install_dir` opt-out in **11
      pre-existing fixtures**. That is real friction for any publisher creating a
      `%ProgramData%` directory, and they should meet it in the release notes
      rather than in a failed install. **Hand the text to REL** for
      `CHANGELOG.md`'s known-limitations section — REL merges before you.

- [ ] **Step 4:** Lane **S7** owns R44 and will send you replacement text for
      `docs/guides/uninstaller.md`'s R44 caveat once its fix lands. If S7 has not
      merged by the time you finish, **leave the existing caveat alone** and say so
      — a caveat describing a fixed bug is better than a doc promising a fix that
      did not ship.

- [ ] **Step 5: Commit.**

## Lane DOC definition of done

- [ ] `docs.yml` green — it fails on generated-doc drift
- [ ] Every flag and filename written cites the code line it came from
- [ ] The corrected silent-install line was **executed**, or the parser-level
      substitute was run and stated as such
- [ ] `sigil-docs/` gone; CODEOWNERS points at `/docs/architecture/`
- [ ] `docs/plan/**` content unchanged apart from the two banners
- [ ] `docs/guides/install-steps.md`, `schemas/sigil-schema.json`,
      `docs/manifest-reference.md`, and `examples/**` **untouched** (S4 owns the
      latter three; `install-steps.md` was S2's in Stage 1 and is **S6's** now —
      still not yours)
- [ ] R55: the five surviving `parameters.install_dir` snippets converted, and the
      false "auto-inserted" claim deleted from both migration guides with the code
      line cited
- [ ] The `directory_create` friction note handed to REL, not written here

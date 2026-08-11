# Stage 2 — Security depth

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> Global constraints live in [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints)
> and apply to every task here.

**Goal:** Close the remaining security findings — network trust, update replay,
uninstall honesty, the secret channel, and the post-v1 hardening items the RC
scope pulled forward.

**Architecture:** Four lanes. S4 owns the manifest/schema and update protocol,
S5 owns residual engine behaviour, S6 owns step-level hardening plus the two
findings that are genuine design decisions and get ADRs rather than patches, and
**S7 owns the two rows that both need declared data resolved from the signed blob
at replay time** (added after Stage 1 — see the amendment note below).

**Runs after:** Gate G1. **Runs in parallel with:** Stage 3 (no shared files).
**Merge order at G2:** S4 → S5 → S6 → **S7** → (then Stage 3's lanes).
S7 is last of the four because it rebases onto S5's `UninstallEngine` changes and
S6's `ScopeLayout` root set.

> ## AMENDED 2026-08-11, after Stage 1 closed
>
> This document was written before Stage 1 ran. Three things changed under it.
>
> **1. Fourteen rows filed during Stage 1 had no owner here.** R44 and R45–R57
> are now assigned — see the lane table and the "Rows added after this document
> was written" block in each lane. **R44 is the schedule risk**: its deadline is
> *before or with the first shipped build containing S2*, and S2 has already
> merged, so the `%ProgramData%\MyApp` guidance in `docs/guides/` is shipping
> broken until it lands.
>
> **2. Every line citation below predates Stage 1's ~440 new tests and its
> rewrites of `UninstallStateStore`, `UninstallEngine`, `RollbackJournal`,
> `InstalledStateResolver`, `AuthenticodeVerifier` and `NativeRuntimeBootstrap`.**
> Spot-checked at amendment time: `Launcher.cs:37-47` is still exactly right,
> `UninstallEngine.cs:42-60` now lands on `RunAsync`'s signature, and
> `RollbackJournal.cs:48-63` has shifted. **Re-derive every citation; do not
> trust one because it is written down.** Where a citation and the code disagree,
> the code wins and the disagreement is worth reporting.
>
> **3. Two premises were re-verified and DO still hold**, so do not "fix" them:
> `DiagnosticCodes.cs`'s highest used code is still **SIG0322**, so S4's
> "SIG0323+" is correct; and S2 already added `allow_outside_install_dir` to the
> schema in Stage 1 — **S4 should know that field exists** rather than adding it
> again.

| Lane | Model | Branch | Findings |
|---|---|---|---|
| `S4` network + update | opus-5 | `rc/s4-network-update` | R8, R13, R14, R30, R37, R39 · **+R45, R46, R47, R49** |
| `S5` residual engine | **opus-5** | `rc/s5-residual-engine` | R15, R18, R28, R29, R34, R38 · **+R48 (fix only), R53, R56, R57** |
| `S6` step hardening + ADRs | opus-5 | `rc/s6-step-hardening` | R33, R35, R36 · **+R50, R52, R54** |
| `S7` signed anchorage | opus-5 | `rc/s7-signed-anchorage` | **R44, R51** |

**S5 was sonnet-5 and is now opus-5.** Its scope grew: R15 caps the whole
"silently unremovable" class — four separate Stage 1 defects each arrived at that
end state by a different route — it must consume S1's `ReplayRefusalCode`
contract correctly, and the document's own R18 task already pre-flagged an
escalation to opus. Three signals pointing the same way is a sizing error, not
bad luck.

**Cross-lane rules:** `schemas/sigil-schema.json` + `docs/manifest-reference.md`
+ `examples/**` are **S4's alone**. S6's `json_edit` schema change (R35) routes
**through S4** — S6 writes the runtime and the test, S4 lands the schema edit in
its lockstep commit; **S7's blob-schema additions route the same way**. S5 and S7
both extend `RollbackJournal.cs` and `UninstallEngine.cs`, which S1 rewrote in
Stage 1: **rebase on the merged RC first**, and consume S1's `RefusedRecords`
rather than reinventing it.

**S5 × S7 is the one file collision in this stage.** S5 owns
`UninstallEngine.cs`'s *outcome* handling (R15: capture per-record results,
retain state on failure) and S7 owns its *anchoring* input (R44/R51: widen
`ReplayAnchorage` from the signed blob). They meet in `ReplayAnchor`. **S7 merges
after S5** and rebases; if the two need to agree on a shape, S7 adapts to what S5
landed, not the reverse.

## The `ReplayRefusalCode` contract — read this before consuming it

S1 published this as **public API**. Three things are not obvious from the type:

1. **The enum has 12 members and they are explicitly numbered. Add, never
   renumber.** Take it as of the merged RC, not from any summary.
2. **`Message` is operator-facing prose. Never parse it** — that is what `Code`
   is for. `Target` for `unregister_com` is attacker-supplied text and must be
   rendered as untrusted.
3. **`RefusedRecords` is not persisted.** If R15 needs refusals to survive for a
   retry, that is new work, not a property you can assume.

---

# Lane S4 — network + update

**Read first:** rows R8, R13, R14, R30, R37, R39;
`Core/Configuration/ManifestParser.cs:150-160`, `:1085-1110`;
`schemas/sigil-schema.json:48-55`, `:457-471`, `:620-628`;
`Installer.Host/Services/HttpOptionsLoader.cs`; `Update/ChannelManifest.cs`,
`ChannelManifestParser.cs`, `UpdateRunner.cs` (post-S3), `UpdateSeams.cs`;
`Cli/Commands/Templates/full-config.yaml:42`;
`docs/architecture/adr-009-update-manifest-signature.md`.

**Before touching the schema, read `.claude/skills/schema-change/SKILL.md`.** The
step-`type` enum appears in **multiple** places in the schema file, and
`docs/manifest-reference.md` + `examples/**` +
`tests/SigilBuild.Schema.Tests/` fixtures must move in the same commit or
`pr-guards`' `schema-lockstep` job fails the PR.

> **S4 carries a proof obligation the other lanes do not — and this paragraph's
> original premise is now HALF FALSE, corrected 2026-08-11.**
>
> It used to say every run had taken the early-exit-when-schema-untouched branch
> at `.github/workflows/pr-guards.yml:53-56`. That is no longer true: lane S2's
> merge `4505b24` changed `schemas/sigil-schema.json` together with
> `docs/manifest-reference.md` and `examples/**`, so the comparison path has now
> run and **passed**.
>
> **The `exit 1` path is still unexercised**, so the obligation stands — but do
> not "verify" the corrected claim by observing a pass, which is what the
> original wording would have led you to do. **S4 is no longer the first lane to
> touch the schema; it is the first to deliberately touch it WRONG.** Before
> landing the full lockstep chain, push a commit that changes
> `schemas/sigil-schema.json` **alone** and confirm the job **fails** naming the
> missing companions; then add the companions and confirm it passes. Record both
> run URLs. Same principle as G0's throwaway PR: a gate nobody has seen fail is
> not a gate.

## File structure

| File | Action | Responsibility |
|---|---|---|
| `Core/Configuration/ManifestParser.cs` | Modify | https enforcement for `source.url` and `manifestUrl`; `signingKey` shape check |
| `Core/Diagnostics/DiagnosticCodes.cs` | Modify | new codes — next free band is **SIG0323+** |
| `schemas/sigil-schema.json` | Modify | scheme constraints; `json_edit.value_type` (for S6) |
| `Installer.Host/Services/HttpOptionsLoader.cs` | Modify | re-check the substituted URL before GET |
| `Update/ChannelManifest.cs`, `ChannelManifestParser.cs` | Modify | freshness fields, inside the signed range |
| `Update/UpdateRunner.cs` | Modify | verify before parse; enforce freshness; version-floor edge |
| `docs/architecture/adr-011-update-manifest-freshness.md` | Create | the replay-protection decision |
| `docs/manifest-reference.md`, `examples/**`, Schema.Tests fixtures | Modify | lockstep |

---

## Task S4.1: HTTPS on `source.url` (R8)

**Interfaces:**
- Produces: a new diagnostic code. Read `DiagnosticCodes.cs` and take the next
  free value at or above **SIG0323** — do not reuse SIG0234, which already means
  "source block missing required fields".

- [ ] **Step 1: Write the failing test**

In `tests/SigilBuild.Core.Tests/` (match the neighbouring parser-test file's
namespace and using placement):

```csharp
[Fact]
public void Parameter_source_url_must_be_https()
{
    var yaml = """
        app: { id: com.example.app, name: App, version: 1.0.0, publisher: Example }
        installer:
          parameters:
            - name: edition
              type: string
              source:
                url: "http://example.com/editions.json"
                items_path: "$.items"
                value_property: "id"
                label_property: "name"
        """;

    var result = ManifestParser.Parse(yaml, "sigil.yaml");

    result.Diagnostics.Should().Contain(d =>
        d.Severity == DiagnosticSeverity.Error && d.Code == DiagnosticCodes.ParameterSourceInsecure);
}
```

> Read an existing parser test first for the exact `ManifestParser.Parse`
> signature and the minimal valid manifest preamble — the YAML above is
> illustrative of shape, and the required `app:` fields must match what the
> schema actually demands.

- [ ] **Step 2: Run, watch fail** — today `ManifestParser.cs:1092-1107` checks
      presence only. This is the **only** HTTP consumer with no scheme check;
      `http_download` (SIG0235) and `packageUrl`
      (`ChannelManifestParser.cs:82-85`) both enforce it at pack *and* run time.
- [ ] **Step 3: Implement** — reject non-`https://` in the parser, and add the
      constraint to **both** schema locations (`:51` and `:624`).
- [ ] **Step 4: Re-check at run time** — `HttpOptionsLoader.LoadAsync` must
      re-validate the substituted URL before the GET, mirroring
      `HttpDownloadStep.cs:37-40`. Pack-time-only validation misses a URL built
      from tokens.
- [ ] **Step 5: Lockstep + commit** — update `docs/manifest-reference.md`,
      any affected `examples/**`, and the Schema.Tests fixtures in the same
      commit.

## Task S4.2: HTTPS on `updates.manifestUrl` (R14)

- [ ] **Step 1:** Same shape as S4.1 — failing test asserting an `http://`
      `manifestUrl` fails to pack.
- [ ] **Step 2: Run, watch fail** (`ManifestParser.cs:156` passes it straight
      through; `schemas/sigil-schema.json:457-461` constrains only
      `"format": "uri"` **despite its own description saying "HTTPS URL"**).
- [ ] **Step 3: Implement** at pack time and re-check in `UpdateSeams` before
      the fetch. **Step 4: Lockstep. Step 5: Commit.**

## Task S4.3: Update-manifest freshness (R13) — design work

This is a protocol change, not a patch. **Write the ADR first**, then implement
what it decides.

- [ ] **Step 1: Write `docs/architecture/adr-011-update-manifest-freshness.md`**
      following the format of `adr-009-update-manifest-signature.md`. It must
      record: the replay threat (a correctly-signed stale manifest lets an
      on-path attacker freeze updates indefinitely, or push an intermediate
      *vulnerable* version that is still newer than installed); the chosen
      mechanism; and the clock-skew tolerance.

      Choose between a signed `issuedAt`/`expiresAt` validity window and a
      monotonic `sequence` persisted client-side. Recommendation: **both** — the
      window bounds freeze attacks without client state, the sequence prevents
      rollback to an older-but-unexpired manifest. State the tolerance
      explicitly; a window with no skew allowance breaks on any misconfigured
      clock.

- [ ] **Step 2: Write the failing tests**

```csharp
[Fact]
public async Task A_replayed_expired_manifest_is_rejected()
{
    // Correctly signed, but issued outside the validity window.
    var manifest = TestChannelManifest.SignedWith(
        TestKeys.ChannelKeyPair,
        version: "2.0.0",
        issuedAt: DateTimeOffset.UtcNow.AddDays(-90));

    var outcome = await UpdateRunner.RunForTestAsync(manifest, installedVersion: "1.0.0");

    outcome.ExitCode.Should().NotBe(0);
    outcome.Log.Should().Contain("stale");
}

[Fact]
public async Task A_manifest_with_a_lower_sequence_than_last_seen_is_rejected()
{
    // ... persist sequence 5, then serve a validly-signed manifest with 4
}
```

> Read the existing `ChannelManifestVerifierTests` first — it already has
> fixture helpers for signing test manifests. Reuse them rather than building
> new ones.

- [ ] **Step 3: Run, watch fail. Step 4: Implement.**

  **The invariant that must not break:** every new field goes **inside the
  signed byte range**. The current scheme signs the whole document
  (`UpdateRunner.cs:102,105,116` — same byte array captured, parsed, and
  verified) and the audit confirmed every consumed field is covered. Adding a
  field the client reads but the signature does not cover would reintroduce
  exactly the class of bug this codebase currently gets right. Persist the
  sequence in **machine-scope** state, and note that S1 hardened that directory
  — use `StateDirectorySecurity.CreateHardened`.

- [ ] **Step 5: Run, watch pass. Commit** with the ADR in the same commit.

## Task S4.4: Verify before parse (R39), and the version-floor edge (R37)

- [ ] **Step 1: Write the failing tests** — (a) a malformed **unsigned**
      manifest is rejected as a signature failure, not a parse failure (proving
      verification ran first); (b) with a `minFromVersion` floor declared and an
      **unparseable** installed version, the update is treated as not-eligible.
- [ ] **Step 2: Run, watch fail. Step 3: Implement** — swap
      `UpdateRunner.cs:105` (parse) and `:116` (verify). Not exploitable today
      since no parsed field is used before verification, but verify-then-parse
      is the cheaper invariant to keep true as the code grows. For R37, change
      `:141-163` so an incomparable installed version fails the floor instead of
      skipping it.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Task S4.5: Fix the `signingKey` template (R30)

- [ ] **Step 1: Write the failing test** — packing a manifest whose
      `signingKey` is a file path produces an error diagnostic.
- [ ] **Step 2: Run, watch fail** — the value goes from
      `ManifestParser.cs:158` to `ExeWrapperPackager.cs:389` with no format
      check.
- [ ] **Step 3: Implement** — add a pack-time diagnostic that `signingKey`
      decodes as base64 **and** imports as a P-256 SPKI. Then fix
      `Cli/Commands/Templates/full-config.yaml:42`, which currently reads
      `signingKey: ./keys/update-signing.ed25519` — a private-key **file path**,
      naming the **wrong algorithm**, contradicting
      `schemas/sigil-schema.json:469-471` ("never a private key, and never a file
      path"). Replace with a base64 SPKI placeholder and a comment pointing at
      the key-generation instructions.
- [ ] **Step 4: Verify `sigil init --template full` output packs. Step 5: Commit.**

## Rows added after this document was written — S4

Full evidence is in `00-GAP_REGISTER.md`; these are the constraints that are not
obvious from the row text. All four came out of lane S3's work on downloaded-binary
trust, so they share a subject with S4's update protocol.

### R45 — declare the downloaded-binary signature policy (priority 1)

S3's own lane call: **R45 and R48 are the two it would not ship without.** Today
the gate reads `SignDeclared` — "did the publisher configure signing for their own
output" — as a proxy for "should downloads be verified". Different questions.

Add `installer.require_signed_downloads`, **defaulting to `SignDeclared`** so no
existing manifest changes behaviour, blob-carried and pack-time validated. This is
S4's own schema commit, so no routing needed.

### R46 — revocation is suppressible by a blackholed responder (priority 2)

**The only row in this wave with a live adversary.** `RevocationUnavailable` is
not a refusal, so anyone who can blackhole two hostnames suppresses revocation of
a stolen signing key. Four candidate mechanisms are named in the register; **one
of them (OCSP stapling via the signed channel manifest) lands squarely in S4's
own protocol work**, which is why this row is here rather than with S6.

Pick deliberately and write down why. **Do not let the current default stand by
inattention** — that is how it got here.

### R47 / R49 — record the decisions, do not necessarily build them

- **R47**: one `fdwRevocationChecks` constant serves the security gate and the
  cosmetic trust line, which want opposite policies. If you split them, note the
  trap: **a cache-only trust line that renders identically to an online-verified
  one reintroduces R17's bug in a new place.**
- **R49**: `WinVerifyTrust` accepts any machine-trusted chain, including a root
  any non-administrator can install. So R11's fix is **integrity, not publisher
  identity**. Pinning needs an authenticated publisher identity in the pack-time
  manifest, which does not exist. If ADR-011 is being written anyway, this belongs
  in it as a stated limitation rather than as new code.

## Lane S4 definition of done

- [ ] Build clean, suite green, format clean; `pr-guards` `schema-lockstep` green
- [ ] The existing `ChannelManifestVerifier` suite passes **unchanged** — that
      code is verified sound and must not regress
- [ ] Every new manifest field is inside the signed byte range (state how you
      confirmed it)
- [ ] `sigil init --template full` produces a manifest that packs
- [ ] ADR-011 committed
- [ ] Negative tests confirmed failing on the parent commit

---

# Lane S5 — residual engine

**Read first:** rows R15, R18, R28, R29, R34, R38; `Engine/UninstallEngine.cs:42-60`
and `RollbackJournal.cs:48-63`, `:106-118` **as merged by S1**;
`Engine/Elevation.cs:92-96`, `:143-155`; `Steps/RunProgramStep.cs:41-62`;
`Engine/Launcher.cs:37-47`; `Engine/SetupInstanceLock.cs:49-93`;
`Engine/FilesInUse.cs:209-210`.

**Rebase on the merged RC before starting.** S1 rewrote `RollbackJournal` and
`UninstallEngine`; build on that, and consume the `RefusedRecords` collection S1
introduced rather than adding a parallel mechanism.

Six independent fixes, **one commit each** so review stays legible. Ordered by
value.

## Task S5.1: Uninstall must not claim success it did not achieve (R15)

- [ ] **Step 1: Write the failing test** — a journal whose `remove_service`
      record names a nonexistent service must produce a non-Ok outcome **and**
      leave `uninstall.json` on disk.

  Why this matters: today `RollbackJournal.cs:111` swallows every per-record
  exception, `UninstallEngine.cs:50` never inspects the result, `:59` deletes
  the state file unconditionally, and the three P11 undos ignore spawn failures
  and exit codes. A failed removal therefore leaves a **permanent SYSTEM
  scheduled task**, machine COM registration, or open firewall port — with the
  only record that could have removed it deleted.

- [ ] **Step 2: Run, watch fail. Step 3: Implement** — capture per-record
      outcomes (extend S1's `RefusedRecords` into a fuller result type), surface
      failures to the user and the log, and **retain** the state file when any
      record failed so a retry is possible. Check exit codes on the
      `schtasks`/`netsh`/`sc` undos.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Task S5.2: Never launch the app with the installer's admin token (R29)

- [ ] **Step 1: Write the failing test** — when de-elevation is unavailable, the
      launch is **skipped**, not performed.
- [ ] **Step 2: Run, watch fail** — `Launcher.cs:43-47` falls through to
      `TryLaunchDirect`, handing the app the installer's admin token with no log
      line. The primary path (Explorer's primary token via
      `CreateProcessWithTokenW`, `:79-176`) is correct; the fallback silently
      un-does it, and silently violates P2's own acceptance criterion.
- [ ] **Step 3: Implement** — skip the launch, log a warning, surface a notice
      on the Done screen. **Step 4: Verify. Step 5: Commit.**

## Task S5.3: Get secrets off the command line (R18)

- [ ] **Step 1: Write the failing test** — a secret parameter value does not
      appear in the argument vector handed to the elevated child.
- [ ] **Step 2: Run, watch fail** — `Elevation.cs:92-96` re-emits
      `/P<secret>=<value>` on the UAC relaunch, and `RunProgramStep.cs:51-56`
      puts resolved secrets in child command lines. Both are visible to
      process-creation auditing (Sysmon/EDR/WMI). Logs, journal, and state are
      already correctly redacted — this is the one channel redaction cannot
      reach.
- [ ] **Step 3: Implement** — pass secrets to the elevated child over an
      inherited pipe or a DPAPI-protected temp file. Document that
      `run_program` arguments are **not** a secret channel.

  **If the elevation-relaunch channel turns out to need a design decision
  (handle inheritance across `ShellExecuteW`-with-`runas` is not
  straightforward), STOP and report rather than improvising.** This is the one
  task in the lane that may need to escalate to opus.

- [ ] **Step 4: Verify. Step 5: Commit.**

## Task S5.4: Decide the `.sigil-bak` contract (R28)

**Do not simply delete the stashes.** They are *required* for uninstall to
restore a pre-existing file the install overwrote. `DiscardTransientStashes`
(`RollbackJournal.cs:48-63`) deliberately skips `RestoreFile` records — that is
correct behaviour with no lifecycle story, not a bug.

- [ ] **Step 1: Decide and write it down** — either move the stashes into the
      per-app state directory (out of Program Files, and S1 hardened that
      directory), or discard them on the success path and document that
      uninstall cannot restore pre-existing files. Recommendation: **move
      them** — losing restore-on-uninstall is a real capability regression.
      Record the decision as a comment on `DiscardTransientStashes` and a note
      in the uninstaller guide (coordinate with DOC, which owns that file — send
      it the text rather than editing).
- [ ] **Step 2: Write the test** for whichever contract you chose.
- [ ] **Step 3: Implement. Step 4: Verify. Step 5: Commit.**

## Task S5.5: Mutex fail-open (R34)

- [ ] **Step 1: Write the failing test** for the `NULL`-handle branch.
- [ ] **Step 2: Run, watch fail** — `SetupInstanceLock.cs:77-82` returns a
      non-owning sentinel indistinguishable from a real lock, so two installs
      can proceed concurrently. `ERROR_ALREADY_EXISTS` (`:85`) correctly fails
      closed; it is the `NULL` branch, which is what a DACL-denied squat
      produces, that fails open.
- [ ] **Step 3: Implement** — distinguish `ERROR_ACCESS_DENIED` (treat as
      contention, distinct message) from other failures; log which branch was
      taken. **Step 4: Verify. Step 5: Commit.**

## Task S5.6: Restart Manager session key (R38)

- [ ] **Step 1:** `FilesInUse.cs:209-210` passes a mutated managed `string` to
      `RmStartSession` — `[LibraryImport]` with UTF-16 marshalling pins the
      string's own buffer and the API writes 32 chars + NUL into it. The size is
      exact and `new string(char,count)` is never interned, so there is no
      overflow **today**; it is one refactor away from corrupting an interned
      literal.
- [ ] **Step 2:** Change to a `char[33]`/`Span<char>` with a `ref char` p/invoke
      signature. **Step 3:** Confirm the existing files-in-use tests still pass —
      there is no new behaviour to test here, only a removed hazard.
- [ ] **Step 4: Commit.**

## Rows added after this document was written — S5

### R56 — hook-phase refusal notices go nowhere

`ctx.ProgressSink` is set only by `InstallEngine`, so the disarm and
staging-refusal notices raised during a `pre_install` or uninstall hook are
reported to nothing. **A security refusal that is not logged is indistinguishable
from a silent success** — the exact failure mode R1 and R19 were fixed to remove,
surviving in the one phase nobody checked. Small fix, and it sits directly beside
S5.1's work on making uninstall honest.

### R48 — get the trust-line lookup off the wizard's UI thread (FIX ONLY)

**You own the fix. You do NOT own the measurement, and you must not claim it.**

The stall is real and measured: **335 ms on the happy path** — online, warm
certificate cache, embedded-signed target — with runs 2 and 3 at 6–9 ms. That is
already past the ~100 ms at which a UI reads as unresponsive, and every condition
that makes it worse (cold cache, captive portal, unreachable CRL distribution
point) moves in one direction only. **That number settles whether to fix it; you
do not need the worst case to proceed.**

The worst-case figure needs an offline, cold-cache first run on real hardware and
belongs to the human partner. **If you find yourself running a quick verification
and getting a fast result, you have measured a warm cache** — a catalog-signed
Windows binary returns `NoSignature` in 0 ms without ever reaching a revocation
lookup, and anything under ~200 ms means the setup was wrong. Report the fix;
leave the number alone.

You are already in `Installer.Host` for R29's Done-screen notice, which is why
this sits here rather than with the other trust rows in S4.

### R53 — an elevated process replays user-scope state at all

`PerformReinstallCleanupAsync` with `_scope == User`. R1 clause (b) stopped a
*machine* operation crossing into `%LocalAppData%`; this is the different question
of whether an elevated run should replay user-scope state **even when the scope is
genuinely user**. The records are anchored, so this is not the pre-R1 hole.
**POST-v1 — a decision is the deliverable, code is optional.** If you conclude it
should stay, say why in the code.

### R57 — a test deletes an HKLM key

`UninstallEngineTests.cs:76`, in a swallowing `finally`, against
`HKLM\…\Uninstall\sigil.test.<guid>`. Pre-existing and harmless today because no
test creates that key — but **CI runs elevated**. You are already in this file for
R15; delete the line while you are there.

## Lane S5 definition of done

- [ ] Build clean, suite green, format clean
- [ ] One commit per fix
- [ ] Negative tests for R15, R29, and R28's chosen contract, each confirmed
      failing on the parent commit
- [ ] If R18 escalated, say so explicitly rather than shipping a partial fix

---

# Lane S6 — step hardening + ADRs

**Read first:** rows R33, R35, R36; `Steps/XmlEditStep.cs:42-45`;
`Steps/JsonEditStep.cs:160-169`; `Steps/Win32/ComRegistration.cs:9-29`, `:66-101`;
`Steps/Win32/ComRegisterStep.cs:62`.

## Task S6.1: Make the XXE posture explicit and bounded (R33)

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void Xml_edit_refuses_a_document_declaring_a_dtd()
{
    var xml = """
        <?xml version="1.0"?>
        <!DOCTYPE lolz [ <!ENTITY lol "lol"> <!ENTITY lol2 "&lol;&lol;&lol;&lol;"> ]>
        <config><item>&lol2;</item></config>
        """;

    var act = () => XmlEditor.SetAttribute(xml, "/config/item", null, "value");

    act.Should().Throw<XmlException>("DtdProcessing must be Prohibit — an internal " +
        "DTD subset in a config file the elevated installer reads is a " +
        "billion-laughs vector");
}
```

> Read `XmlEditStep.cs` for the actual editor entry point and its signature —
> the name above follows the `IniEditor.Set` / `ConfigEditorTests` convention
> but **verify it**.

- [ ] **Step 2: Run, watch fail.** Today `new XmlDocument{…}` + `LoadXml`
      relies on a framework default: on .NET 10 `XmlDocument.XmlResolver`
      defaults to `null`, so external-entity file disclosure and SSRF are
      **already blocked** — but that is an unasserted default, not a stated
      invariant, and the internal DTD subset is still parsed with no expansion
      cap. There is no `XmlResolver`/`DtdProcessing` assignment anywhere in the
      repo.
- [ ] **Step 3: Implement** — set `XmlResolver = null` explicitly and load via
      `XmlReader.Create` with `DtdProcessing = DtdProcessing.Prohibit`. The
      explicit assignment is the point: it turns a default into an invariant a
      future framework change cannot silently revoke.
- [ ] **Step 4: Run, watch pass. Step 5: Commit.**

## Task S6.2: `json_edit` value typing (R35)

- [ ] **Step 1: Write the failing test** — a string value that happens to look
      like JSON is written as a **string**, not as structure.
- [ ] **Step 2: Run, watch fail** — `JsonEditStep.cs:163` calls
      `JsonNode.Parse(value)`, documented as intentional literal inference. But
      a value sourced from a wizard field or a `registry_read` var writes an
      object/array/`true` where the manifest author expected a string — a
      type-confusion channel into the app's own config.
- [ ] **Step 3: Implement** `value_type: string|json`, defaulting to
      **`string`** (the safe default; the current behaviour becomes opt-in).
- [ ] **Step 4: Route the schema change through S4** — send S4 the field
      definition and the `manifest-reference.md` text. Do **not** edit
      `schemas/sigil-schema.json` yourself; the lockstep job will fail the PR
      that touches it without its companions, and S4 owns that commit.
- [ ] **Step 5: Verify, commit** (runtime + test only).

## Task S6.3: Decide `com_register`'s trust model (R36)

- [ ] **Step 1: Write `docs/architecture/adr-012-com-registration-isolation.md`.**

  The facts to record: `DllRegisterServer` executes **in-process at high
  integrity** (`ComRegistration.cs:66-101`), so a malformed or hijacked
  publisher DLL crashes or takes over the installer rather than a disposable
  child. The current choice is deliberate and documented at
  `ComRegistration.cs:9-29` (AOT/interop rationale — `regsvr32` in a child
  would be the alternative).

  Decide: keep in-process and document the trust assumption, or move to a child
  process. Weigh AOT-safety (the `[LibraryImport]` + function-pointer path was
  the one AOT-risk step in P11) against blast radius. Either answer is
  defensible; an *undocumented* answer is not.

- [ ] **Step 2:** If the ADR chooses isolation, implement it with a test
      asserting a failing DLL does not take down the host. If it chooses
      in-process, add the trust assumption to `docs/guides/install-steps.md`'s
      `com_register` section — **coordinate with S2**, which owns that file in
      Stage 1; by Stage 2 S2 has merged, so edit it directly but rebase first.
- [ ] **Step 3: Verify. Step 4: Commit** with the ADR.

## Rows added after this document was written — S6

### R50 — the native-runtime cache fallback is never reclaimed

R4's fix falls back to a per-run GUID directory when the shared cache root cannot
be established or repaired. A squat that cannot be repaired — a *file* at that
path, or an owner-pinned deny ACE — is **not** a security hole (that refusal is
the design), but the fallback is never cleaned up, so one command by any
unprivileged user arms an unbounded **~18 MB per install** disk leak.

**Two constraints killed the original attempt at this, and will kill yours:**
guards must read from an **open handle** with reparse-point checks, **never
through the path**; and a reclaim must not race a concurrent install that is
using the directory it is about to delete. If you cannot meet both, say so and
leave the row open — a reclaim that deletes a live install's DLLs is worse than
the leak.

### R52 — `ScopeLayout` models one install root, containment accepts three

`ScopeLayout.cs:61-65` hardcodes `SpecialFolder.ProgramFiles`. Lane S2 accepted
both `%ProgramFiles%` roots in `IsContained` and **correctly declined to widen a
shared surface mid-lane**. The result is that permitted destinations and the
default destination are described in two places and can drift. Give `ScopeLayout`
a root *set* and derive containment from it.

**This is shared surface — S5 and S7 both read `ScopeLayout`.** Change it early in
the lane and tell them, rather than landing it at the end.

### R54 — `shortcut_create.location`'s explicit-path branch has no containment

Pre-existing; outside S2's task list. The named anchors (`start_menu`, `desktop`,
…) are contained, an explicit path is not. Straightforward, and it belongs beside
your other step-level work.

## Lane S6 definition of done

- [ ] Build clean, suite green, format clean
- [ ] ADR-012 committed with an explicit decision, not a survey
- [ ] R35's schema change handed to S4, not landed here
- [ ] R52's `ScopeLayout` change landed **early** and announced to S5 and S7
- [ ] R50 either fixed under both constraints, or explicitly left open with the
      reason — no half-reclaim
- [ ] Negative tests confirmed failing on the parent commit

---

# Lane S7 — signed anchorage (R44, R51)

**New lane, added 2026-08-11.** Neither row existed when this document was
written; both were filed during Stage 1.

**Merges after S5** and rebases onto it. Where the two must agree on a shape in
`ReplayAnchor`, S7 adapts to what S5 landed.

**Read first:** rows R44 and R51 in `00-GAP_REGISTER.md` — read them in full, the
reasoning matters more than the requirement; `Engine/ReplayAnchor.cs`,
`ReplayAnchorage.cs`, `UninstallEngine.cs` and `WrapperBlob.cs` **as merged by
S1**; and `docs/guides/uninstaller.md`'s existing caveat.

## Why these are one lane and not two

Both rows have the same shape and the same rejected alternative.

- **R44**: lane S2 shipped `allow_outside_install_dir` as a documented manifest
  opt-out — and **documents `%ProgramData%\MyApp` as the example** — but S1's
  replay anchor has no counterpart, so every such record is refused at uninstall
  while the ARP row and the state are deleted anyway. The app becomes silently
  unremovable, through a door neither lane owned.
- **R51**: registry replay anchoring is a **denylist that is not converging**.
  Three consecutive review rounds each produced another key shape it had missed —
  `Classes\…\shell\open\command`, `App Paths\*`, then `txtfile`, `lnkfile`,
  `mscfile`, `Drivers32`. S1 was told to stop adding names and deny the *shape*,
  which bought time; it did not close the class.

**The naive fix for both is the same, and is unsafe for the same reason.** A
per-record marker saying "I was declared out-of-tree" / "this key was declared" is
a record saying **do not anchor me** — and the journal is the untrusted artefact,
so a planted journal would opt itself out of the entire mechanism R1 exists to
build. Anything the journal asserts about its own permissions is worthless.

**So the durable shape, for both:** resolve the declared out-of-tree roots and the
declared registry-key allowlist from the **SIGNED BLOB** at replay time, and widen
`ReplayAnchorage` with them. **The journal records nothing new.** Building this
twice is why the rows are one lane.

## Task S7.1: get the signed blob to the replay

- [ ] **Step 1:** Establish how `UninstallEngine` can reach the blob today. It
      cannot — that is the whole reason S1 filed R44 rather than fixing it. Decide
      the mechanism and **write it down before writing code**.
- [ ] **Step 2:** The invariant that must not break: **nothing is trusted from the
      journal.** If your design has a code path where a journal field influences
      which roots are permitted, the design is wrong, not the implementation.
- [ ] **Step 3:** If this needs a blob-schema addition, **route it through S4**
      exactly as S6's R35 does. S4 owns the lockstep commit.

## Task S7.2: widen `ReplayAnchorage` from the blob (R44)

- [ ] **Step 1: Write the failing test** — an install that `file_copy`s into
      `%ProgramData%\MyApp` under `allow_outside_install_dir` must have that
      record **replayed**, not refused, at uninstall. Today it is refused.
- [ ] **Step 2: Run, watch fail. Step 3: Implement. Step 4: Verify.**
- [ ] **Step 5:** Confirm the refusal suite did not erode. S1's measure was that
      forcing the anchoring predicate to `Allow` fails 51 tests. **Widening the
      anchor must not lower that number by more than the tests that legitimately
      change meaning — and you must name each one that does.**

## Task S7.3: replace the registry denylist with a blob-declared allowlist (R51)

- [ ] **Step 1: Write the failing tests** — the four names the denylist most
      recently missed (`txtfile`, `lnkfile`, `mscfile`, `Drivers32`) must be
      refused under the allowlist without being named in it. **That is the point:
      an allowlist that still needs those names written down has not changed the
      shape of the problem.**
- [ ] **Step 2: Run, watch fail. Step 3: Implement. Step 4: Verify.**
- [ ] **Step 5:** Keep the denylist as defence in depth or delete it — either is
      defensible, but say which and why.

## Lane S7 definition of done

- [ ] Build clean, suite green, format clean
- [ ] **Nothing is trusted from the journal** — state how you confirmed it, not
      that you intended it
- [ ] R44's negative test confirmed failing on the parent commit
- [ ] R51's allowlist refuses the four escaped names **without naming them**
- [ ] The `Allow`-forcing refusal count is reported, with every changed test named
- [ ] Any blob-schema change routed through S4, not landed here
- [ ] `docs/guides/uninstaller.md`'s R44 caveat handed to DOC as replacement text
      (DOC owns that file) — do not edit it here

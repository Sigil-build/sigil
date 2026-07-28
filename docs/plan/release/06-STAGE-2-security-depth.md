# Stage 2 — Security depth

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> Global constraints live in [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints)
> and apply to every task here.

**Goal:** Close the remaining security findings — network trust, update replay,
uninstall honesty, the secret channel, and the post-v1 hardening items the RC
scope pulled forward.

**Architecture:** Three lanes. S4 owns the manifest/schema and update protocol,
S5 owns residual engine behaviour, S6 owns step-level hardening plus the two
findings that are genuine design decisions and get ADRs rather than patches.

**Runs after:** Gate G1. **Runs in parallel with:** Stage 3 (no shared files).
**Merge order at G2:** S4 → S5 → S6 → (then Stage 3's lanes).

| Lane | Model | Branch | Findings |
|---|---|---|---|
| `S4` network + update | opus-5 | `rc/s4-network-update` | R8, R13, R14, R30, R37, R39 |
| `S5` residual engine | sonnet-5 | `rc/s5-residual-engine` | R15, R18, R28, R29, R34, R38 |
| `S6` step hardening + ADRs | opus-5 | `rc/s6-step-hardening` | R33, R35, R36 |

**Cross-lane rules:** `schemas/sigil-schema.json` + `docs/manifest-reference.md`
+ `examples/**` are **S4's alone**. S6's `json_edit` schema change (R35) routes
**through S4** — S6 writes the runtime and the test, S4 lands the schema edit in
its lockstep commit. S5 extends `RollbackJournal.cs` and `UninstallEngine.cs`,
which S1 rewrote in Stage 1: **rebase on the merged RC first**, and consume
S1's `RefusedRecords` rather than reinventing it.

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

> **S4 carries a proof obligation the other lanes do not.** Stage 0's final
> review established that `schema-lockstep`'s **failing** path has never
> executed — every run so far has taken the early-exit-when-schema-untouched
> branch at `.github/workflows/pr-guards.yml:53-56`. The branch was traced by
> hand and no bug was found, but it is untested code on a load-bearing gate.
> **S4 is the first lane to touch the schema, so it is that gate's
> proof-of-failure.** Before landing the full lockstep chain, push a commit that
> changes `schemas/sigil-schema.json` **alone** and confirm the job **fails**
> naming the missing companions; then add the companions and confirm it passes.
> Record both run URLs. Same principle as G0's throwaway PR: a gate nobody has
> seen fail is not a gate.

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

## Lane S6 definition of done

- [ ] Build clean, suite green, format clean
- [ ] ADR-012 committed with an explicit decision, not a survey
- [ ] R35's schema change handed to S4, not landed here
- [ ] Negative tests confirmed failing on the parent commit

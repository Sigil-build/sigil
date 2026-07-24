# ADR-009: ECDSA P-256 for channel-manifest signatures

- **Status:** Accepted
- **Date:** 2026-07-23
- **Decision driver:** P12 (T12.1–T12.3) — the `/Update` runtime needs to
  trust a channel manifest fetched over the network from a host the
  installed application does not otherwise control at runtime. `Functions.cs`
  and `Evaluator.cs` already draw a hard AOT/size boundary around the wrapper
  runtime (ADR-008 §1.2/§5); this ADR picks the concrete signature primitive
  for the update engine within that boundary.

---

## Decision (TL;DR)

The channel manifest fetched at `/Update` time is signed with **ECDSA
P-256**, verified using only the **.NET base class library**
(`System.Security.Cryptography.ECDsa`) — no third-party cryptography package,
no native dependency, and (critically) **no reference from the AOT wrapper
runtime to `SigilBuild.Signing` or `NSec.Cryptography`** at all. The trust
anchor is `updates.signingKey`, a value read from the **app manifest at pack
time** and stamped into the produced `Setup.exe`; the manifest host — which is
untrusted infrastructure from the installed app's point of view — never gets
to supply or negotiate its own key.

---

## Context

Sigil already has a signature primitive: `SigilBuild.Signing.Local.ZipManifestSigner`
signs/verifies zip artifacts with **Ed25519** via the `NSec.Cryptography`
package (`SigilBuild.Signing.csproj`'s `PackageReference`). `NSec.Cryptography`
wraps libsodium — a native (P/Invoke) dependency. `SigilBuild.Signing` is a
CLI-side, non-AOT-constrained project: it runs as part of `sigil sign`, a
normal .NET process, not inside the installer host.

The update engine is the opposite kind of code. `ChannelManifestVerifier`
(`src/SigilBuild.Wrapper.Core/Update/ChannelManifestVerifier.cs`) runs
**inside the Native-AOT wrapper runtime** — the same
`SigilBuild.Wrapper.Core` assembly that ships in every produced `Setup.exe`
and is bound by:

- **Native AOT / trim safety** (`AGENTS.md` hard rule 1): no reflection-based
  crypto, no `Activator.CreateInstance`, nothing the AOT/trim analyzer would
  reject as `IL2026`/`IL3050`.
- **The installer host's 45 MB size gate**
  (`scripts/publish-installer-runtime.ps1`), currently sitting at
  **~42 MB measured** (ADR-008 §5.2's amendment log; ~3 MB headroom as of
  P9). Anything the update engine adds to this footprint is not free —
  every prior lane that touched it (localization, P9) had to re-pin the gate
  consciously.
- **Zero new project references from `SigilBuild.Wrapper.Core`.** Today it
  references only `SigilBuild.Core` and (as a source-generator-only,
  analyzer reference) `SigilBuild.Localization.Generator`. It does **not**
  reference `SigilBuild.Signing`, and pulling that project in transitively
  drags `NSec.Cryptography` (a native libsodium binding) and `Azure.Identity`
  (`SigilBuild.Signing.csproj`'s other dependency, entirely irrelevant to
  update verification) into the AOT-published installer host.

Reusing `ZipManifestSigner`/`NSec`/Ed25519 as-is for channel-manifest
verification was the first option considered, precisely because it is
already the codebase's existing signature primitive. It does not survive
contact with the constraints above.

---

## Alternatives considered

### 1. Reuse `ZipManifestSigner` / Ed25519 via `NSec.Cryptography`

**Rejected.** `NSec.Cryptography` ships native libsodium binaries per RID.
Referencing `SigilBuild.Signing` (or vendoring `NSec` directly) from
`SigilBuild.Wrapper.Core` would require:

- Native-AOT publishing libsodium's native shim alongside the installer
  host — untested against the AOT/trim analyzer, and a real risk of an
  `IL2026`/`IL3050` failure or an outright runtime crash on first use if the
  native asset resolution doesn't line up with the AOT single-file layout.
- A meaningful, uncertain size cost against a 45 MB gate that already has
  only ~3 MB of headroom — unlike ADR-008 §5.2's localization case (a known,
  measured ~2.26 MB), the native libsodium payload's AOT-published footprint
  was not something this task could respend headroom on without displacing
  a real budget decision onto a documentation task.
- A new project reference (`SigilBuild.Wrapper.Core` → `SigilBuild.Signing`)
  that couples the installer runtime to the CLI-side signing stack
  (`Azure.Identity` and all), which today is deliberately kept out of the
  AOT-published binary entirely.

None of this is a hard impossibility — it is a real, unbounded engineering
cost for a decision that has a strictly cheaper alternative below.

### 2. NSec/libsodium directly, without going through `SigilBuild.Signing`

**Rejected for the same reason as (1).** Vendoring `NSec.Cryptography`
straight into `SigilBuild.Wrapper.Core` still adds a native dependency and
the same AOT-publish risk; routing around `SigilBuild.Signing`'s project
reference avoids the `Azure.Identity` transitive weight but not the
native-binary risk, which is the dominant cost.

### 3. ECDSA P-256 via the .NET BCL — **adopted**

`System.Security.Cryptography.ECDsa` is part of the base class library Sigil
already ships with on every target RID — it needs **zero new
`PackageReference`, zero new `ProjectReference`, and zero native asset**.
Concretely, verification is:

```csharp
using var ecdsa = ECDsa.Create();
ecdsa.ImportSubjectPublicKeyInfo(spki, out _);
if (ecdsa.KeySize != 256) { /* reject: not P-256 */ }
bool ok = ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
```

— all BCL surface, all AOT-safe (no reflection, no dynamic dispatch), no
platform-specific native shim. `ChannelManifestVerifier.Verify` (see
`src/SigilBuild.Wrapper.Core/Update/ChannelManifestVerifier.cs`) is exactly
this, plus mapping every expected failure mode to `SIG0321` instead of an
escaping exception.

### 4. No signature at all, integrity via `sha256` only

**Rejected outright, not seriously considered.** `sha256` on the *package*
only proves the package matches what the channel manifest *says* it should
be — it proves nothing about whether the channel manifest itself is
authentic. Without a manifest signature, any party that can influence DNS,
a CDN, or a compromised hosting account for `manifestUrl` could redirect
every installed copy of an app to an attacker-chosen "latest version" whose
`sha256` they also control. The manifest signature is the actual security
boundary; `sha256` is package-integrity hygiene on top of it, not a
substitute for it.

---

## Decision detail

### Signature encoding

- **Format: detached.** The signature is never embedded in the channel
  manifest JSON. It is fetched as a sibling HTTP resource at
  `manifestUrl + ".sig"`. This keeps the manifest itself a plain, generic
  JSON document (no signature-envelope schema to design or version) and
  lets the manifest and its signature be cached/served independently.
- **Bytes signed: the exact fetched manifest bytes, no canonicalization.**
  `UpdateRunner` verifies over the identical `byte[]` it parses — there is no
  JSON canonicalization (key sorting, whitespace normalization, etc.) step.
  This is deliberate: canonicalization is itself a surface that can go wrong
  (two "equivalent" JSON documents that canonicalize differently between the
  signer's and verifier's implementations is a known class of signature
  bypass). Byte-exact verification has no such ambiguity — whatever bytes
  the HTTP response body contains are both what gets parsed and what gets
  verified.
- **Encoding: base64 IEEE P1363 (`r‖s`, 64-byte).** This is .NET's *default*
  `ECDsa.SignData`/`VerifyData` signature format
  (`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`) — a fixed-width
  concatenation of the two 32-byte signature components, not an ASN.1/DER
  `SEQUENCE`. Choosing the BCL default means the signer side (whatever
  produces `.sig` files — a publisher's own signing script, using the same
  BCL API) needs no DER encoder/decoder, and the verifier needs no
  hand-rolled ASN.1 parsing either — both sides just call the same BCL
  method with its own default.
- **Public key encoding: base64 X.509 SPKI (DER).** `updates.signingKey` is
  `ECDsa.ExportSubjectPublicKeyInfo()`'s output, base64-encoded — the
  standard, self-describing public-key container that names its own curve,
  so `ImportSubjectPublicKeyInfo` alone is sufficient; the verifier still
  independently checks `KeySize == 256` afterward so a key that imports
  without error but isn't P-256 (e.g. a P-384 SPKI) cannot slip through
  merely because the import call didn't throw.

### Trust anchor: pack-time embedding, not manifest-supplied

`updates.signingKey` is a field of the **app's own `sigil.yaml`**, read by
`ExeWrapperPackager` at pack time and stamped into the produced `Setup.exe`
(`WrapperBlob.UpdateSigningKey` → the `SIGIL_BLOB_V1` resource). The
already-installed application is therefore the sole source of truth for
which public key is trusted, and that key never changes without the
publisher shipping (and the user installing) a new version.

This is the property that makes the manifest signature meaningful at all:
the channel-manifest **host** — which by definition must be reachable over
the network, and is exactly the kind of infrastructure that can be
compromised, mis-configured, or DNS-hijacked independently of the publisher's
own build pipeline — is never in a position to assert "trust this key,"
because it never supplies the key. It can only serve content the
already-embedded key either does or does not verify. A hostile or
compromised manifest server can at most deny service (serve nothing / serve
garbage, which fails closed per `SIG0320`/`SIG0321`) — it cannot get a
victim to accept an attacker-chosen "latest version" by presenting an
attacker-chosen key alongside it.

### Every failure mode is a typed, non-throwing reject

`ChannelManifestVerifier.Verify` never lets `CryptographicException`,
`FormatException` (malformed base64), or `ArgumentException` (wrong key/sig
shape) escape — each is caught and mapped to
`ChannelManifestVerifyResult.Failed` carrying `DiagnosticCodes.ChannelManifestSignatureInvalid`
(**SIG0321**), kept a distinct exit code (`InstallSession.UpdateManifestRejectedExitCode`,
**8**) from a plain network/operational failure (**7**) — see
`docs/guides/updates.md`. The failure message never includes key or
signature bytes/text (redaction-safe, per ADR-008 §3's transitive-secrecy
spirit, even though `signingKey` itself is a public key, not a secret — the
discipline of never echoing raw crypto material into a log is kept uniform).

---

## Consequences

- **Zero size/dependency cost.** No new `PackageReference`, no new
  `ProjectReference` from `SigilBuild.Wrapper.Core`, no native asset added to
  the AOT-published installer host. The 45 MB host gate
  (`scripts/publish-installer-runtime.ps1`) is unaffected by this decision —
  unlike ADR-008 §5.2's localization case, there is no re-pin to record here.
- **`SigilBuild.Signing`'s Ed25519/`NSec` path is unchanged and unrelated.**
  `ZipManifestSigner` continues to sign zip artifacts for `sigil sign`; this
  ADR does not touch it, retire it, or ask it to be reused. The CLI-side
  signing stack and the AOT runtime's update-verification stack are and stay
  two different primitives for two different trust boundaries (a build-time
  tool with no size constraint, vs. code shipped inside every installed
  app).
- **Publishers need an ECDSA P-256 key pair, not an Ed25519 one, to sign
  channel manifests.** This is a one-time setup cost per publisher,
  documented in `docs/guides/updates.md`, and orthogonal to whatever key
  they use for Authenticode/`sigil sign`.
- **No canonicalization step is a constraint on manifest hosting, not just
  the verifier**: whoever generates the `.sig` file must sign the *exact*
  bytes that will later be served at `manifestUrl` — re-serializing/
  re-formatting the JSON after signing (e.g. a CDN that minifies or
  re-indents JSON in flight) would break verification. This is called out in
  `docs/guides/updates.md`.

---

## Verification

- `ChannelManifestVerifierTests` (see `tests/SigilBuild.Wrapper.Tests/Update/`)
  exercise: a valid signature verifying, a tampered manifest failing, a
  wrong-curve key being rejected despite importing cleanly, malformed
  base64 in either the key or the signature, and a missing key/signature
  both surfacing `SIG0321` rather than throwing.
- The AOT/trim analyzer (Release-only, `IL2026`/`IL3050` as errors per
  `AGENTS.md`) enforces that no reflection-based crypto path was
  (re-)introduced — `ECDsa.Create()`/`ImportSubjectPublicKeyInfo`/
  `VerifyData` are all trim-safe BCL surface, so this holds by construction
  rather than by a dedicated test.
- The installer host's 45 MB size gate in
  `scripts/publish-installer-runtime.ps1` is the standing CI enforcement
  that this decision added no measurable weight — a future change that
  *does* add cryptography-related weight must re-pin that gate consciously,
  the same discipline ADR-008 §5.2's amendment log records for
  localization.

---

## Amendment log

| Date | Change | Justification |
|------|--------|----------------|
| 2026-07-23 | Initial decision: ECDSA P-256 via BCL `ECDsa`, detached base64 IEEE P1363 signature over exact manifest bytes, base64 X.509 SPKI public key, pack-embedded trust anchor. | P12 (T12.1–T12.3) — `/Update`'s channel-manifest verification needed a concrete signature primitive inside the AOT wrapper runtime's size/dependency envelope. |

*(Append one row per future change to the signature scheme. Never rewrite
prior rows.)*

# ADR-011: Freshness and replay protection for the channel manifest

- **Status:** Accepted
- **Date:** 2026-08-11
- **Decision driver:** Register rows **R13** (no freshness or replay protection
  on the signed channel manifest), **R45** (the downloaded-binary signature
  policy is inferred rather than declared) and **R46** (a blackholed CRL/OCSP
  responder suppresses revocation). ADR-009 settled *who* signed the channel
  manifest; it deliberately said nothing about *when* it was signed. This ADR
  closes that, and records the two adjacent trust decisions that turn out to
  share the same shape: a security posture that was inferred, silently, from
  something else.
- **Also records:** the stated limitations for **R47** and **R49**, which are
  filed rather than built.

---

## Decision (TL;DR)

1. **R13.** The channel manifest gains three **required** fields — `issuedAt`,
   `expiresAt` and `sequence` — inside the signed byte range. The client
   enforces a validity window with a **±5 minute** clock-skew tolerance and an
   independent **30-day maximum age** ceiling, and refuses any manifest whose
   `sequence` is lower than the highest it has previously accepted. The high
   water mark is persisted in machine-scope state through
   `StateDirectorySecurity.CreateHardened`.
2. **R45.** `installer.require_signed_downloads` makes the downloaded-binary
   signature policy **declared**, with `sign_declared` as the default so no
   existing manifest changes behaviour.
3. **R46.** The chosen mechanism is **opt-in hard-fail**, spelled
   `require_signed_downloads: always_verified_revocation`. The other three
   candidates are rejected with reasons below.
4. **R47, R49.** Stated limitations, not code. See the last section.

---

## Context: the signature says *who*, never *when*

ADR-009 gave the channel manifest a detached ECDSA P-256 signature over its
exact bytes, and the audit confirmed every consumed field is covered by it.
That is a complete answer to "did the publisher mint this document" and *no*
answer at all to "is this document current".

Before R13 the manifest carried no timestamp, expiry, nonce or sequence
(`Update/ChannelManifest.cs`), and the only monotonicity check anywhere was
against the *locally installed* version (`Update/UpdateRunner.cs`, the
`UpgradePlanner.Decide` call). No "highest version ever seen" was persisted.
So an on-path attacker, a compromised CDN, or anyone holding the DNS name could:

- **Freeze updates indefinitely** by replaying yesterday's correctly signed
  manifest. The client reports "up to date" and exits 0 while a security fix
  exists. Nothing in the protocol distinguishes this from the genuine case, and
  it is completely silent.
- **Steer the client onto a known-vulnerable intermediate version** by replaying
  a signed manifest for a version that is older than current but still newer
  than installed. The version comparison is satisfied, the signature is
  genuine, and the client installs it.

Neither requires breaking any cryptography. Both are pure replay.

---

## Decision detail: R13

### The two mechanisms are complementary, so both ship

A validity window and a monotonic sequence fail in opposite directions, which
is exactly why the recommendation was to take both:

| | Window alone | Sequence alone |
|---|---|---|
| Freeze attack | **Bounded** — a replayed manifest stops working at expiry | Caught only once a higher sequence has been seen; a client that never saw one is stuck |
| Rollback to an older, *unexpired* manifest | **Not caught** — the replayed document is still inside its own window | **Caught** |
| Needs client-side state | No | Yes |
| Works on first contact | Yes | No (nothing to compare against) |

The window does the work that requires no memory; the sequence does the work
the window structurally cannot. Shipping one would have left a named,
understood hole.

### All three fields are REQUIRED, not optional

This is the part most easily got wrong. **An optional freshness field is
defeated by replaying a manifest that predates it.** If `issuedAt` were
optional, an attacker would simply replay a correctly signed pre-R13 manifest —
which has no `issuedAt`, therefore no window, therefore no expiry — and the
whole defence evaporates without a single forged byte. Requiring the fields
makes such a document *malformed* (SIG0320), which is the only formulation that
actually closes the downgrade.

The cost is a deliberate, one-time break of the channel-manifest wire format.
It is acceptable because the publish stage that would mint these documents does
not exist yet (`AGENTS.md`: "The publish stage and delta-update SDK are not
built yet"), so there is no deployed corpus to break. `schemaVersion` stays at
`1`: it signals what the *runtime* understands, and older clients ignore unknown
JSON fields, so a manifest carrying the new fields is still readable by a
pre-R13 client. The compatibility direction that matters — new client refusing
an old manifest — is the defence itself, not a regression.

### Timestamps are parsed strictly

`DateTimeOffset.TryParse` with `InvariantCulture` accepts `01/02/2026` and
resolves it by culture convention — two different days depending on the reader —
and accepts a timestamp with no offset at all, which is up to 26 hours of
ambiguity at each window edge. A validity window is a security boundary, and a
security boundary whose meaning depends on the reader's locale is not one.
`ChannelManifestParser.TryParseTimestamp` therefore uses `TryParseExact` against
an explicit ISO-8601 format list requiring a `Z` or a numeric offset. Note the
format strings spell the literal `Z` as `'Z'` and not as `K`: `K` also matches
the *empty* string and would silently readmit the zone-less form.

### Clock skew: ±5 minutes

A window with no skew allowance breaks on any misconfigured clock, and a machine
with a wrong clock is overwhelmingly more common than one under an on-path
replay. Five minutes is the familiar Kerberos-era allowance and is three orders
of magnitude below the timescale over which a freeze attack is interesting
(days, not minutes), so granting it costs the defence nothing measurable.
Future-dated manifests are refused rather than tolerated beyond that: refusing
beats guessing which clock is wrong.

### Maximum age: 30 days, enforced independently of `expiresAt`

`expiresAt` is publisher-chosen, so a publisher who sets it to the year 3000 —
by mistake, or on the advice of a "make the warnings stop" answer — silently
opts out of the entire defence. `MaxManifestAge` is the ceiling the client
enforces regardless of what the document declares.

### The sequence is recorded when the manifest is believed, not when the install succeeds

The high water mark records the newest manifest this machine has **seen and
authenticated**, which is what a later replay must beat. Whether the package it
advertised went on to install cleanly is a different question, and coupling the
two would let a failed install reopen the replay window.

### Every new field is inside the signed byte range — and how that was confirmed

`UpdateRunner.RunAsync` captures the fetched body **once**
(`var manifestBytes = manifestFetch.Bytes;`) and hands that same array to
`ChannelManifestVerifier.Verify` and to `ChannelManifestParser.Parse`. There is
no sidecar file, no HTTP header, and no out-of-band channel from which any
field is read. Consequently *every* field declared on the `ChannelManifest`
record is covered by the signature **by construction** — the property holds for
the three new fields for the same structural reason it already held for
`version`, `packageUrl` and `sha256`, not because of anything specific done for
them. Adding a field the client reads but the signature does not cover would
require introducing a second source of bytes, which this design has never had.

### Where the sequence lives

`%ProgramData%\Sigil\<AppId>\update-sequence.txt` for machine scope, created via
`StateDirectorySecurity.CreateHardened` — S1 hardened that directory precisely
because `%ProgramData%`'s inherited DACL grants `BUILTIN\Users` write, and an
unprivileged user who can lower the stored sequence re-enables every replay this
row closes. It is a separate file from `uninstall.json` on purpose: the high
water mark must survive an uninstall/reinstall cycle, because forgetting it is
exactly the reset an attacker wants.

User scope keeps the value in the user's own profile without hardening, and this
is **not** a security boundary there — the same posture R37 records for
`minFromVersion`. A user who can rewrite their own HKCU install record can steer
their own update eligibility regardless; that is publisher policy, not a
defence against a third party.

Reads and writes are fail-safe: an unreadable or corrupt high water mark reads
as "none seen" rather than throwing, because a machine that cannot read its own
state must still be able to take a genuine update. The bounded cost is that an
attacker who can *delete* the file resets the sequence defence — which, on a
hardened machine-scope directory, requires the administrator rights that make
the question moot.

---

## Decision detail: R45 — declare the policy

The gate in front of every downloaded binary read `SignDeclared`: "did this
publisher configure signing for their own output". It used that as a proxy for
"should downloads be verified". **Those are different questions**, and a
publisher who signs nothing got no verification on anything they downloaded and
ran elevated.

`installer.require_signed_downloads` names the policy directly:

| Value | Meaning |
|---|---|
| `sign_declared` *(default)* | Arm the gate iff this manifest declares a `sign` block — the pre-R45 inference, preserved exactly |
| `always` | Arm the gate regardless |
| `always_verified_revocation` | `always`, plus an unestablished revocation status is a refusal (R46) |

The default is `sign_declared` **specifically so that adding this field changes
no existing manifest's behaviour**. The value is carried in the wrapper blob —
inside the Authenticode-signed artifact — because the policy governing what an
installer will run must not be editable by whoever is attacking the download.

An unrecognized value is SIG0326 rather than a silent fallback to the default:
quietly ignoring a typo in a setting that decides whether a downloaded binary is
checked before it runs elevated would be the same silent-disarm that R45 exists
to close.

---

## Decision detail: R46 — the mechanism choice

**This is the only row in this wave with a live adversary.**
`RevocationUnavailable` is not a refusal, so anyone who can blackhole two
hostnames — a captive portal, a compromised resolver, a hostile corporate
network — suppresses revocation of a *stolen signing key*. Four mechanisms were
named. They were not equally available.

### Rejected: OCSP stapling via the signed channel manifest

The strongest option and the right long-term direction: the publisher fetches an
OCSP response for the code-signing certificate at publish time and embeds it in
the channel manifest, where the existing signature already covers it. The
freshness proof then travels *with* the signed document instead of being fetched
from a host the attacker controls, which defeats blackholing outright.

Rejected for this wave on three concrete blockers, not on preference:

1. **There is no publish stage to staple in.** `AGENTS.md` states the publish
   stage is not built. Nothing in the tree could fetch and embed an OCSP
   response today.
2. **It requires an OCSP response parser.** RFC 6960 `BasicOCSPResponse` is
   ASN.1 DER; the client would have to match `CertID`
   (issuerNameHash/issuerKeyHash/serialNumber) and verify the responder's
   signature *and* its delegation from the CA. .NET exposes no public
   OCSP-response parser, so this means hand-rolling security-critical ASN.1
   parsing of attacker-influenced input, under Native AOT. That is a large,
   high-risk piece of work and emphatically not a side effect of another row.
3. **It covers one of three call sites.** Only the update package flows through
   the channel manifest; prerequisites and the web-stub payload do not.

Recorded here as the intended successor so the next person does not have to
rediscover the reasoning.

### Rejected: Microsoft's disallowed-certificate list

**Already implemented.** `WinVerifyTrust` consults the machine's `Disallowed`
store, and a hit surfaces as `AuthenticodeStatus.Revoked`, which
`DownloadedBinaryTrust.Decide` already treats as a refusal with no opt-out —
not even `allow_unsigned`. Adopting this as "the R46 fix" would be recording a
no-op. It also does not address the threat: the disallowed list is a curated
list of high-profile compromises, not a substitute for per-certificate
revocation, and its own sync is network-dependent.

### Rejected: known-good caching with transition detection

Remember that a certificate thumbprint was once verified with revocation
successfully established, and treat a later `RevocationUnavailable` on that same
thumbprint as a suspicious *transition*. No protocol change, no publish stage.

Rejected because **it does not fire in the case that matters.** The realistic
attack is an attacker signing a new malicious build with the stolen key and
blackholing the responder; the victim machine has never seen that certificate
before, so the lookup takes the "unknown thumbprint → proceed" branch. The
mechanism only helps from the second encounter onward, while adding a new
persisted trust store — new attack surface, a new class of poisoned-cache bug,
and more value to an attacker who beats the state-directory ACLs S1 just
hardened.

### Adopted: opt-in hard-fail

`require_signed_downloads: always_verified_revocation` makes
`RevocationUnavailable` a refusal. It composes with the R45 field this same
change introduces, needs no new protocol, no new parser, and no new persisted
state.

The default stays permissive, and that is a deliberate, documented choice rather
than inattention. Refusing by default would mean an installer behind a captive
portal, on an air-gapped network, or inside a locked-down enterprise egress
cannot install **anything** — a far more likely outcome than the attack it would
prevent, and the plan is explicit that anchoring which breaks real installs is
worse than the bug it closes. The publisher is the only party who knows whether
their audience is reliably online, so the publisher is who gets the switch.

**What this does NOT do, stated plainly:** it does not defeat the adversary for
publishers who leave the default. It converts an undocumented global default
into a documented publisher choice, and makes the strict posture *reachable*,
which it previously was not at any price. That is a smaller claim than
"revocation is no longer suppressible", and it should not be read as the larger
one.

---

## Stated limitations (filed, not built)

### R47 — one `fdwRevocationChecks` constant serves two callers

The security gate wants to be strict and online; the wizard's cosmetic
"Signed by …" line wants to be fast and never block. They share one constant.
Splitting them is **post-v1** and is not done here.

**The trap for whoever splits them:** a cache-only trust line that renders
*identically* to an online-verified one reintroduces R17's bug in a new place.
R17's whole defect was that the operator could not tell "verified" from "could
not check". A faster trust line that silently loses that distinction is the same
bug with a better latency number. If the line is made cache-only, it must *say*
so.

### R49 — Authenticode validity is integrity, not publisher identity

`WinVerifyTrust` accepts any chain the machine trusts — **including a root any
non-administrator can install into their own store**. So "Authenticode-valid",
as used by R11's gate and by the wizard's trust line, means *the bytes were not
altered after signing*. It does **not** mean the expected publisher signed them.

Publisher pinning would need an authenticated publisher identity in the
pack-time manifest, which does not exist. Filed rather than half-built: a
pinning check against an unauthenticated name would read as identity
verification while providing none, which is worse than the honest gap.

**Nobody should read R11's fix as identity verification.** It is a tamper check
with a trust-store-shaped edge, and the `require_signed_downloads` values above
inherit exactly that meaning — `always` means "must be Authenticode-valid", not
"must be signed by you".

---

## Consequences

- The channel-manifest wire format gains three required fields. Any tooling that
  mints channel manifests must emit them; there is no such tooling in-tree yet.
- A publisher whose clock is wrong by more than 5 minutes at mint time produces
  a manifest that is refused. This is intended and is reported as a stale/
  not-yet-valid refusal naming both timestamps.
- A machine that has accepted sequence N will refuse anything below N for that
  app, permanently, until the state file is removed by an administrator. A
  publisher who needs to roll back must mint a *higher* sequence, not a lower
  one. This is the defence working; it is also a foot-gun and is called out in
  the manifest reference.
- `UpdateRunner` gained an `IUpdateSequenceStore` seam. It is optional and
  defaults to the real file-backed store — **tests must pass an in-memory one**,
  because the default reads and writes a real `%ProgramData%` path and CI runs
  elevated.

## Verification

- `UpdateFreshnessTests` — window, skew, max-age, future-dating, sequence
  rollback, and the end-to-end replayed-expired-manifest case.
- `ChannelManifestParserTests` / `UpdateFreshnessTests` — the three fields are
  required and strictly parsed.
- `DownloadPolicyTests` — R45's default equals the behaviour it replaces, R46's
  opt-in refusal, and the over-refusal controls (`Trusted` and `NotEvaluated`
  still allowed under the strictest policy; `Revoked` refused under all).
- `NetworkTrustParseTests` — SIG0326 for an unknown policy value, plus every
  declared value accepted.
- **`ChannelManifestVerifierTests` passes unchanged.** That suite is marked
  verified-sound in the register; it exercises signature mathematics only and
  never reaches the parser, so none of this touches it.

## Amendment log

- 2026-08-11 — initial version (lane S4, Stage 2).

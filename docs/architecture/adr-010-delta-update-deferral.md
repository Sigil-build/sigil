# ADR-010: Delta-update deferral

- **Status:** Accepted (deferral, not a rejection — see "Intended follow-up
  shape" below)
- **Date:** 2026-07-23
- **Decision driver:** P12 (`01-IMPLEMENTATION_PLAN.md`'s P12 section,
  gap G15/G16 in `00-GAP_ANALYSIS.md`) ships the update engine's
  full-package path (T12.1–T12.6). `UpdatesSection.DeltaTargets` and the
  `deltaTargets:` manifest field have existed since before P12 as parsed,
  schema-validated, but functionally inert metadata — this ADR records that
  they **stay** inert for this release and describes the shape a later delta
  engine would need to take to consume them.

---

## Decision (TL;DR)

Sigil's update engine ships **full-package updates only** in this release.
`/Update` always downloads the complete new version's `Setup.exe`, verifies
its `sha256`, and runs it — the same artifact a fresh install would use, just
version-newer. **Delta (binary-diff) patches are explicitly deferred**:
`deltaTargets` remains a parsed, schema-validated manifest field with zero
runtime effect, and no zstd-dictionary patch format exists yet. This is a
scoping decision, not an architectural dead end — §"Intended follow-up
shape" below records the reuse seam already left in place for it.

---

## Context

`UpdatesSection.DeltaTargets` (`src/SigilBuild.Core/Manifest/UpdatesSection.cs`)
and the schema's `updates.deltaTargets` (`schemas/sigil-schema.json`,
"How many previous versions to generate delta patches against", default `3`,
range `0`–`20`) predate this ADR — they were added, parsed, and
schema-validated before the update *engine* that would consume them existed.
P12's job was to make `/Update` do something real for the first time
(T12.1–T12.6: channel manifest, ECDSA verification — see
[ADR-009](adr-009-update-manifest-signature.md) — version comparison,
full-package download and hand-off, plus the web-installer stub). The
original P12 scoping (`01-IMPLEMENTATION_PLAN.md`) is explicit that delta
patches are a separate, larger effort:

> Delta updates (`deltaTargets`, zstd dictionaries) stay **explicitly
> deferred** — ship full-package updates first, write the delta-deferral
> ADR (T12.7).

This ADR is that record. It exists so a future contributor who finds
`deltaTargets` in the schema and manifest reference does not have to
reverse-engineer whether it does something today (it does not) or guess at
what a delta engine would need to look like.

### Why defer rather than build both together

A delta-update system is a materially larger, differently-shaped problem
than full-package updates, on every axis P12 already had to reason about:

1. **A new binary-diff format and its own security model.** A delta patch is
   not just "a smaller download" — it is a set of instructions to transform
   an installed binary into a different one. That transform must itself be
   integrity- and authenticity-checked (a corrupted or forged patch applied
   to a legitimate installed binary is at least as dangerous as a forged
   full package, arguably more so because the "base" is already trusted disk
   content an attacker doesn't need to smuggle in). Reusing ADR-009's
   ECDSA/channel-manifest trust model is the plan, but the *payload* format
   the manifest would point at does not exist yet and needs its own design
   pass, not a rider on this one.
2. **Per-version dictionary training and storage.** `deltaTargets` names *how
   many* previous versions to target, which implies a publish-time pipeline
   that: keeps N previous full builds available, trains a zstd dictionary
   per (from-version, to-version) pair (or a shared dictionary strategy),
   and publishes N delta artifacts alongside the one full package per
   release. None of that pipeline, storage contract, or publish-command
   surface exists — it is `sigil publish` territory (itself not yet built;
   see `AGENTS.md`: "The publish stage ... [is] not built yet"), not
   something that belongs in the update-engine change alone.
3. **A materially larger AOT-runtime surface for the same size budget.**
   The full-package path's biggest runtime addition was ECDSA verification,
   which ADR-009 shows costs effectively nothing against the 45 MB host
   gate. A delta-*apply* engine (reconstructing a target binary from a base
   + patch under the rollback journal, with its own corruption/mismatch
   handling) is real new code weight and new failure modes to test — a
   different-sized task than "verify a signature and run a downloaded exe."
4. **Full-package-first is the standard bring-up order** for exactly this
   reason — Squirrel, Velopack, and Sparkle all shipped whole-file updates
   before (or instead of) binary deltas, because the whole-file path is
   what makes an update mechanism *correct and trustworthy* first, and delta
   is a bandwidth optimization layered on top once that foundation is
   proven. Shipping delta un-battle-tested alongside the first-ever working
   `/Update` would couple two sources of risk into one release.

Shipping the full-package path now, on its own, means every current
`updates:` user gets a **working, secure** update mechanism today, while the
delta work is scoped and reviewed as its own change against a stable base.

---

## Decision detail

### What ships now

- `deltaTargets` **parses and validates** (schema range `0`–`20`, default
  `3`) but is **read by nothing** at runtime. A manifest author can set it
  today for forward compatibility; it has zero observable effect until a
  delta engine lands.
- The channel manifest contract (ADR-009, `docs/guides/updates.md`) already
  reserves `minFromVersion` — "the lowest installed version this package can
  update from" — which is exactly the field a delta-aware channel manifest
  would also need (a delta patch is only valid from specific base versions).
  Full-package updates use it today as a floor check; a delta engine reuses
  the same field, unchanged.
- `docs/guides/updates.md` documents `deltaTargets` as accepted-but-inert
  and points here for why.

### What is explicitly NOT built

- No delta/binary-diff patch format.
- No zstd **dictionary** mode. `PayloadCodec`
  (`src/SigilBuild.Wrapper.Core/Codec/PayloadCodec.cs`) — the shared zstd
  codec already used by the packager (encode) and the installer host
  (decode) for the `SIGIL_PAYLOAD_V2` payload container — is presently
  **dictionary-free by design** (its own remarks: "a single-threaded,
  dictionary-free `Compressor`"). It is *homed in `Wrapper.Core` so both
  sides call the same implementation, and so the future delta-update engine
  can reuse it* — that reuse seam is already in place; dictionary support
  itself is not.
- No publish-time pipeline for generating/hosting per-version delta
  artifacts (this is `sigil publish` scope, itself not yet built).
- No delta-apply engine in the AOT wrapper runtime.

---

## Intended follow-up shape

Recorded here so a future delta lane has a starting point, not a blank page.
None of this is committed scope for any specific future task — it is the
shape the deferred work is expected to take, consistent with what P12 already
built:

1. **Channel manifest gains a parallel delta entry.** Rather than replacing
   `packageUrl`/`sha256` (the full-package fields), the channel manifest
   would likely gain an optional array of delta candidates, each naming a
   `fromVersion`, a `deltaUrl`, and a `deltaSha256` — so a single channel
   manifest can advertise "full package at X" and "delta from version V to
   X at Y" simultaneously, letting `/Update` prefer a matching delta when
   the installed version qualifies and fall back to the full package
   otherwise. This composes with the existing `minFromVersion` floor rather
   than replacing it.
2. **The delta payload reuses `PayloadCodec`'s framing, extended with
   dictionary support.** `ZstdSharp.Port` (the pure-managed zstd binding
   already a dependency of `SigilBuild.Wrapper.Core`, chosen precisely
   because it needs no native `libzstd` and publishes clean under Native
   AOT) supports dictionary-based compression; today's codec simply never
   passes one in. A dictionary trained against the app's prior release
   (per `deltaTargets`, i.e. against each of the last N versions) shrinks
   the delta payload the same way `SIGIL_PAYLOAD_V2` shrinks a full payload
   today — the codec's container format would grow a dictionary-reference
   field, not a new library dependency.
3. **Delta application still runs under the rollback journal, still hands
   off to a real `Setup.exe`.** Consistent with the full-package path's
   design (the downloaded package is always a real, independently-signed
   `Setup.exe` that performs its own P3 upgrade — `/Update` never
   reimplements install logic), a delta engine's most likely shape is:
   apply the patch to reconstruct a full `Setup.exe` locally (verifying the
   reconstructed file's own `sha256` against the channel manifest before
   ever executing it), then hand off to it exactly as the full-package path
   does today. This keeps `UpdateRunner`'s "verify, then run a real
   installer" trust model intact rather than inventing a second, weaker one
   for the delta case.
4. **Signature/trust model is inherited from ADR-009 unchanged.** Whatever
   channel-manifest shape a delta system adopts, it is signed and verified
   exactly as today's channel manifest is — the delta entries are additional
   fields in the same signed document, not a second document with a second
   trust boundary to design.
5. **Publish-time tooling is a `sigil publish` concern.** Training
   dictionaries, retaining N previous builds, generating per-pair delta
   artifacts, and publishing them alongside the channel manifest belongs to
   the publish stage once it exists, not to the update *engine* covered by
   this ADR.

---

## Consequences

- **Every current `updates:` user gets full-package updates today**,
  correctly signed and verified per ADR-009, with no half-built delta path
  to reason about or accidentally trigger.
- **`deltaTargets` is forward-compatible but currently a no-op.**
  `docs/guides/updates.md` says so explicitly, so a manifest author is not
  misled into thinking setting it changes `/Update`'s behavior.
- **No size-budget impact.** Nothing here adds runtime code; `PayloadCodec`
  is unchanged (dictionary-free), and the AOT host's 45 MB gate is
  untouched by this decision.
- **No new lockstep surface.** This ADR changes no schema, no blob format,
  no step catalog — it is a scoping record, matching ADR-008's own
  "policy ADR, no `src/` behavior changes" precedent for how a deferral gets
  written down.
- **A future delta lane amends this ADR's "Intended follow-up shape" section
  with the actual design** once it is scoped, the same append-only
  discipline ADR-008's amendment log uses, rather than silently ignoring or
  contradicting the reasoning recorded here.

---

## Verification

- `deltaTargets` has schema coverage (`tests/SigilBuild.Schema.Tests/`,
  range `0`–`20`) and manifest-parse coverage
  (`tests/SigilBuild.Core.Tests/Manifest/`) proving it round-trips, but
  **no** runtime test asserts any *effect* from it — that absence is itself
  the expected, verifiable state this ADR describes (there is nothing for a
  behavioral test to exercise yet).
- `PayloadCodec`'s own tests (`tests/SigilBuild.Wrapper.Tests/Codec/` /
  packaging tests referencing it) cover the dictionary-free path only; a
  future delta lane adds dictionary-mode coverage alongside its
  implementation.

---

## Amendment log

| Date | Change | Justification |
|------|--------|----------------|
| 2026-07-23 | Initial deferral: full-package updates ship in P12; `deltaTargets`/zstd-dictionary delta patches recorded as explicitly out of scope, with the intended follow-up shape described. | P12 (T12.7) — `01-IMPLEMENTATION_PLAN.md` required this ADR before P12 could be considered documentation-complete. |

*(Append one row when a future lane actually scopes or builds the delta
engine. Never rewrite prior rows.)*

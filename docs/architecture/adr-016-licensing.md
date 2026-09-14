# ADR-016: Relicense from MIT to the proprietary Sigil License 1.0

- **Status:** Accepted
- **Date:** 2026-09-14
- **Decision driver:** Sigil is about to ship `v0.1.0-alpha`, its first public
  artifact. Under MIT, the moment that artifact exists so does the right for
  anyone to fork the engine, rebrand it, and ship a competitor — and the engine
  is the product. The license had to be decided before the tag, not after,
  because a license change cannot reach copies already distributed.
- **Scope:** replaces `LICENSE`; changes the package license metadata, the
  copyright holder recorded in every source file, the contribution policy, and
  every document that described Sigil as open source. No `src/` behavior
  changes. Supersedes nothing; ADR-016 is the first licensing ADR.

---

## Decision (TL;DR)

Relicense Sigil, effective 2026-09-14, from **MIT** to the **Sigil License
1.0** — a proprietary, source-available license that grants unlimited use of the
tools (including commercial use) and an explicit royalty-free right to
redistribute the artifacts the tools generate, while withholding every right to
copy, modify, or reuse the source code itself. Record `Yevhen Khudoliiv` as the
sole copyright holder in `LICENSE`, in package metadata, and in a per-file
header on every `.cs` and `.axaml` file, enforced by `IDE0073` as a build error.
Close the repository to external code contributions.

---

## Context

### The copyright is undivided, so relicensing is actually available

`git shortlog -sne --all` reports **364 commits from one human** across two
addresses:

```
   300  Yevhen Khudoliiv <eugenkhudoliiv@gmail.com>
    64  Yevhen Khudoliiv <yevhen.khudoliiv@helixleisure.com>
```

There is no third-party contributor whose permission would be needed, and no CLA
to reconstruct after the fact. This is the single fact that makes the whole
decision cheap; it is also the fact the contribution policy below exists to
preserve, because it stops being true the first time an outside pull request
merges.

The ~77 commits carrying `Co-Authored-By: Claude` trailers do not affect this. A
model is not an author under copyright law, and those trailers assert
provenance, not ownership.

### The dependencies do not obstruct it

Every direct NuGet dependency is permissively licensed (MIT, BSD-3-Clause, or
Apache-2.0), and `THIRD-PARTY-NOTICES.md` already enumerates the native
components that carry binary-redistribution attribution requirements — Skia,
HarfBuzz, ANGLE and their transitive pieces. The one copyleft exposure in the
tree, SkiaSharp's eCos/LGPL-2.1 dual-licensed HTTP-server component, is not
used. Nothing in the dependency graph requires Sigil's own code to stay open.

What those terms *do* require is attribution on binary redistribution, and that
obligation passes through to every vendor who ships a `Setup.exe`. The license
must say so rather than quietly inherit it.

### A generic "no redistribution" license would break the product

This is the constraint that disqualified the obvious answer. Sigil's output is
not a document describing an installer; it *is* Sigil's own binary.
`WrapperRuntimeLocator.cs` resolves the Native-AOT-published
`SigilBuild.Installer.Host.exe` for each target RID, and `ExeWrapperPackager`
stamps that executable with the vendor's step blob, payload, icon, and the
bundled native dependencies as a `SIGIL_RUNTIME_V1` resource. The vendor then
ships the result to their own users.

So the ordinary use of Sigil is: **a third party reproduces and distributes the
licensor's compiled code, to an unbounded audience, as a component of their own
product.** An off-the-shelf source-available license that forbids
redistribution — PolyForm Strict 1.0.0, for instance — would make every correct
user of Sigil an infringer for using it as designed, and would make their end
users infringers for running the installer they received. The license needs a
redistribution grant carved out by construction, not by exception.

### The MIT grant already made cannot be recalled

The repository has been public under MIT since **2026-04-30**. Anyone who cloned
it in the intervening four and a half months holds MIT rights to that snapshot
permanently, including the right to fork and redistribute it. Relicensing binds
future versions only.

The exposure is nevertheless small and worth naming precisely: no `v*` tag
exists, no GitHub release has been published, and the only NuGet package is
`SigilBuild 0.0.0-reserved` (published 2026-05-05), a metadata-only placeholder
containing no code. What is at risk is a source snapshot of a pre-MVP tree, not
a shipped product with users.

---

## Decision in detail

1. **`LICENSE` becomes the Sigil License 1.0**, drafted in PolyForm's register —
   plain English, defined terms, short numbered sections — because PolyForm is
   explicitly designed as a family assembled from reusable blocks, and a bespoke
   grant was needed anyway (see below).
2. **Use is unrestricted.** Any purpose including commercial, any number of
   machines, any number of personnel. A distribution tool nobody may run is not
   a product.
3. **Generated Artifacts carry an explicit redistribution grant.** Royalty-free,
   perpetual, to unlimited recipients, conditioned on the Redistributable
   Components being unmodified, being shipped only inside an artifact that
   installs the vendor's own product, and third-party notices being preserved.
   Recipients get a direct right to run what they received, and that right
   survives termination of the vendor's own license — otherwise a vendor's
   breach would retroactively strand their customers.
4. **Source reuse is withheld entirely** — copying, modifying, deriving,
   redistributing the software itself, extracting the Redistributable Components
   from an artifact, and building a competing product from what the source
   teaches. Reverse engineering is carved back where applicable law forbids the
   restriction, rather than asserting an unenforceable prohibition.
5. **Third-party notice obligations are passed through explicitly**, since the
   licensor has no power to waive them and a vendor who strips
   `THIRD-PARTY-NOTICES.md` from their installer breaches Skia's and HarfBuzz's
   terms, not only Sigil's.
6. **Per-file authorship headers** on all sources, naming the holder and the
   license. Volume at the time of writing: **466 `.cs`** files under `src/` and
   `tests/`, plus **16 `.axaml`**.
7. **Contributions are closed.** `CONTRIBUTING.md` states that pull requests are
   not accepted and that issues and bug reports are. Soliciting code for a
   codebase nobody may derive from is incoherent, and each merged outside
   contribution would fracture the sole ownership that section one rests on.

---

## Alternatives considered

**PolyForm Strict 1.0.0, unmodified.** The closest off-the-shelf fit and the
default recommendation for "you may look, you may not take". Rejected because it
grants no redistribution right, and Sigil's entire function is to hand the user
a redistributable binary built from Sigil's own compiled code. Adopting it would
have criminalised the product's happy path. Adding a carve-out as an appendix to
someone else's license text was considered and rejected too: a license read as
"document A except where document B disagrees" is worse to reason about than one
coherent page.

**Two documents — proprietary source license plus a separate Runtime
Redistribution License.** This is how the commercial installer vendors
(InstallShield and peers) structure it, and it is the most defensible split
legally, because the grant that flows to third parties is physically separate
from the grant that binds the licensee. Rejected on proportionality: it makes
every evaluating vendor read two agreements to answer one question, at a stage
where Sigil has no customers and needs adoption friction to be zero. Revisit if
the redistribution grant ever needs to differ by customer tier.

**PolyForm Noncommercial 1.0.0.** Free for noncommercial use, paid otherwise.
Rejected because Sigil's users are, by definition, people shipping commercial
Windows software — the noncommercial carve-out would cover almost nobody, so the
practical effect is "commercial license required to evaluate", and organic
adoption goes to zero.

**BUSL-1.1 with a four-year Apache-2.0 conversion.** The HashiCorp/Sentry
compromise: protection now, open source eventually. Rejected because BUSL is
written around hosted services, and its pivotal term — "production use" — is
close to meaningless for a build-time CLI. A vendor could not tell whether
packaging a nightly build counted. A license whose central condition is
ambiguous for the actual product is a support burden, not a protection.

**All rights reserved, no grant at all.** Maximally protective and three lines
long. Rejected because it would make `v0.1.0-alpha` unusable by anyone who
downloaded it, converting the release from a product into a display case.

**Staying on MIT.** Rejected on the forcing problem in Context: the engine is
the product, and MIT invites a rebranded fork the day the first artifact ships.

---

## Consequences

**What gets easier.** The license question is settled before the first tag
rather than after, which is the only cheap moment it exists in. Package
metadata, the NuGet listing, and the repository's own documentation can be made
to agree on one answer.

**What gets harder.**

- **GitHub will classify the repository as "Other"** instead of showing an MIT
  badge, and license-scanning tools in downstream vendors' pipelines will flag
  Sigil for manual review. That review is a conversation the licensor now has to
  be ready to have; it is the cost of the grant being bespoke.
- **Every new source file needs a header.** Enforced rather than remembered —
  see Verification — but it is friction on file creation, and any code emitted
  by `SigilBuild.Localization.Generator` must carry the header from the emitter,
  because `IDE0073` cannot see generator output.
- **External contributions stop.** Bug reports still arrive; fixes do not.
- **Pre-2026-09-14 snapshots stay MIT forever.** Nothing recovers them. Future
  discussions of "can we stop X from forking" must start from that fact rather
  than relitigate it.

**Size budgets:** unaffected. The change adds a two-line comment to 466 source
files and touches no code path; the `sigil.exe` 15 MB and installer-host 45 MB
gates are unmoved. Header comments do not survive compilation.

**New lockstep surface.** The license identity now appears in `LICENSE`,
`README.md`, `Directory.Build.props` (`Copyright`, `PackageLicenseFile`,
`PackageRequireLicenseAcceptance`), `THIRD-PARTY-NOTICES.md`, `CONTRIBUTING.md`,
`AGENTS.md`, `docs/architecture-overview.md`, and the `.editorconfig` header
template. Changing the license means changing all of them; this row belongs in
`AGENTS.md`'s lockstep table.

**NuGet:** `PackageLicenseExpression` accepts SPDX identifiers only, so a
bespoke license must ship as `PackageLicenseFile` with
`PackageRequireLicenseAcceptance` set. The existing `SigilBuild 0.0.0-reserved`
listing records MIT in its metadata permanently — NuGet packages cannot be
deleted, only unlisted — but it contains no code, and it should be unlisted once
a real version ships.

---

## Verification

- **`IDE0073` is set to `error`** with `file_header_template` in
  `.editorconfig`. Because `Directory.Build.props` sets
  `TreatWarningsAsErrors=true` and `EnforceCodeStyleInBuild=true`, a source file
  without the header fails the build. `dotnet format` applies the header
  automatically, and the **`dotnet format` CI job is a required status check**,
  so the header cannot rot on a merge path.
- **The `.axaml` headers and the localization generator's emitted header have no
  analyzer** and are guarded by review against this ADR's Consequences section.
- **`AGENTS.md` carries the lockstep row**, so an agent changing one license
  surface is told about the other eight.
- **No test asserts the license text**, deliberately: a test that pins a legal
  document's wording would be edited by whoever changes the document, and proves
  nothing a reviewer would not catch.

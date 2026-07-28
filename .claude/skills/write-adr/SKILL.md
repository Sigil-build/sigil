---
name: write-adr
description: Write an Architecture Decision Record for Sigil matching the existing ADR format in docs/architecture/. Use when making or documenting an architectural choice, trade-off, or reversal (engine design, packaging pipeline, AOT strategy, dependencies).
---

# Write a Sigil ADR

Architecture changes without an ADR get bounced by CODEOWNERS review. ADRs live
in `docs/architecture/` — read `adr-avalonia-aot.md` and `adr-008-expression-policy.md`
first and match their voice: heavy on *measured evidence* (sizes, timings, repro
commands) and *rejected alternatives*, not just the conclusion.

## Naming

Newer ADRs are numbered (`adr-008-…`); continue the numbered sequence:
`adr-NNN-short-slug.md` with the next free number.

## Required sections

1. **Status + date** — Proposed / Accepted / Superseded (link the superseding ADR)
2. **Context** — the forcing problem, with concrete numbers where possible
   (Sigil cares about binary size, AOT compatibility, and install-time behavior;
   "it's cleaner" is not context)
3. **Decision** — one paragraph, imperative
4. **Alternatives considered** — each with the reason it lost; this is the most
   valuable section for future agents, do not skip it
5. **Consequences** — including what gets harder, size-budget impact
   (15 MB CLI / 45 MB host gates), and any new lockstep surface
6. **Verification** — how the decision is enforced (CI gate, analyzer severity,
   test) rather than merely remembered

## After writing

- If the ADR changes an enforceable rule, actually wire the enforcement
  (`.editorconfig` severity, `Directory.Build.props`, CI gate) in the same PR.
- Update `AGENTS.md` if the decision adds a hard rule agents must follow.
- Amendments to an existing ADR (like the P9 size re-pin) go in the original
  file as a dated amendment section, not a new ADR — unless the decision reverses.

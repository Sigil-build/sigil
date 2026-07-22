# Sigil — Claude Code

@AGENTS.md

## Claude-specific extras

- Project skills live in `.claude/skills/` — use them instead of improvising:
  - `add-install-step` — the full 9-layer chain for a new step type
  - `schema-change` — any edit to `schemas/sigil-schema.json` and its lockstep surfaces
  - `write-adr` — architecture decision records matching the existing format
  - `aot-safety` — pre-review checklist for Native AOT / trim safety
- Advisory hooks in `.claude/settings.json` warn (never block) when you write
  AOT-risky patterns or touch the schema; treat their feedback as review comments.
- Local machine is Windows; CI is `windows-latest`. If you're in a Linux sandbox,
  say which tests you couldn't run rather than implying a green suite.

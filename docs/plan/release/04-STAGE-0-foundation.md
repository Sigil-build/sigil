# Stage 0 — Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans`. Steps use checkbox (`- [ ]`) syntax.
> Global constraints live in [`03-RC_ORCHESTRATION.md`](03-RC_ORCHESTRATION.md#global-constraints)
> and apply to every task here.

**Goal:** Clear the formatting backlog and turn on the CI gates that were
written but never installed, so every later lane is actually gated.

**Architecture:** One lane, run **alone**. The `dotnet format` pass rewrites 28
files across `src/` and `tests/`; any concurrent lane would conflict with it.
Three mechanical commits, no behaviour change.

**Tech Stack:** `dotnet format`, GitHub Actions YAML, git.

**Lane:** `F0` · **Model:** `claude-haiku-4-5-20251001` · **Branch:**
`rc/f0-foundation` · **Findings:** R20, R40, R41

---

## Why this is a stage of its own

`_agent-setup/github-workflows/pr-guards.yml` implements conventional-commit PR
titles, schema/docs lockstep, and `dotnet format` — and was **never copied into
`.github/workflows/`**. `AGENTS.md:16,96,104` and `CONTRIBUTING.md:69` all claim
these are enforced. They are not, and `main` currently fails the format check
(28 of 465 files).

Installing the gate and clearing the backlog must happen together: installing
the gate alone turns every subsequent PR red.

## File structure

| File | Action | Responsibility |
|---|---|---|
| 28 files under `src/` and `tests/` | Modify | whitespace/ordering only, from the formatter |
| `.github/workflows/pr-guards.yml` | Create | PR-title lint, schema lockstep, format gate |
| `.claude/` (skills, hooks, settings) | Create | project agent config `CLAUDE.md` already references |
| `_agent-setup/` | Delete | staging dir whose own `apply.ps1:25` says to remove it |
| `.gitignore` | Modify | drop the no-op `./docs/`, add `.superpowers/` |

---

## Task 1: Clear the formatting backlog

**Files:**
- Modify: 28 files under `src/` and `tests/` (formatter output only)

**Interfaces:**
- Consumes: nothing
- Produces: a tree where `dotnet format --verify-no-changes` exits 0 — the
  precondition for Task 2's gate to be installable

- [ ] **Step 1: Confirm the backlog before touching anything**

```bash
cd /c/projects/Sigil
git checkout -b rc/f0-foundation release/v0.1.0-alpha
dotnet format Sigil.slnx --verify-no-changes --verbosity diagnostic 2>&1 | tail -20
```

Expected: exit code **2**, ending with `Formatted 28 of 465 files.` Record the
exact count — if it is not 28, the tree moved and you should report before
proceeding.

- [ ] **Step 2: Record the baseline build and test totals**

```bash
dotnet build Sigil.slnx -c Release 2>&1 | tail -5
dotnet test Sigil.slnx -c Release --no-build 2>&1 | grep -E "^(Passed|Failed)!"
```

Expected: `0 Warning(s), 0 Error(s)`; totals summing to **1097 tests — 1096
passed, 1 skipped, 0 failed**. Write these down; Step 5 proves you did not
change them.

- [ ] **Step 3: Run the formatter**

```bash
dotnet format Sigil.slnx
```

- [ ] **Step 4: Review the diff for semantic changes**

```bash
git diff --stat
git diff | grep -E "^[+-]" | grep -vE "^[+-]\s*$" | grep -viE "^[+-]\s*(using|//)" | head -40
```

Expected: whitespace, blank lines, `using` ordering, and brace placement only.
**If any hunk changes an identifier, a literal, an operator, or control flow —
STOP and report.** The formatter should never do that, and if it did, something
else is wrong.

- [ ] **Step 5: Verify nothing broke**

```bash
dotnet format Sigil.slnx --verify-no-changes; echo "format exit=$?"
dotnet build Sigil.slnx -c Release 2>&1 | tail -5
dotnet test Sigil.slnx -c Release --no-build 2>&1 | grep -E "^(Passed|Failed)!"
```

Expected: format exit **0**; build still `0 Warning(s), 0 Error(s)`; test totals
**identical** to Step 2.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "style: clear dotnet format backlog (R20)

28 of 465 files reformatted by dotnet format. Whitespace, using ordering,
and brace placement only — no semantic changes. Build and test totals
unchanged (1097 tests, 1096 passed, 1 skipped).

Prerequisite for installing the pr-guards format gate in the next commit."
```

---

## Task 2: Install the CI gates that were never turned on

**Files:**
- Create: `.github/workflows/pr-guards.yml` (copy of `_agent-setup/github-workflows/pr-guards.yml`)
- Create: `.claude/**` (copy of `_agent-setup/claude-config/**`)
- Delete: `_agent-setup/`
- Modify: `AGENTS.md`, `CONTRIBUTING.md` only if their claims are still wrong after the copy

**Interfaces:**
- Consumes: Task 1's format-clean tree
- Produces: a `pr-guards` workflow gating every later lane's PR

- [ ] **Step 1: Read what you are installing**

```bash
cat _agent-setup/apply.ps1
cat _agent-setup/github-workflows/pr-guards.yml
ls -R _agent-setup/claude-config/
```

`apply.ps1` is the never-run copy step. Read it to confirm the destination
paths; do not necessarily execute it — the copy is two `cp` commands and doing
them by hand is more reviewable.

- [ ] **Step 2: Copy the workflow and the agent config into place**

```bash
mkdir -p .github/workflows
cp _agent-setup/github-workflows/pr-guards.yml .github/workflows/pr-guards.yml
mkdir -p .claude
cp -r _agent-setup/claude-config/. .claude/
ls .claude/ .claude/skills/
```

Expected in `.claude/`: `settings.json`, `hooks/post-edit-guard.sh`, and
`skills/{add-install-step,aot-safety,schema-change,write-adr}/SKILL.md`.

- [ ] **Step 3: Verify the workflow parses and its format job would pass**

```bash
python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/pr-guards.yml')); print('YAML OK')"
dotnet format Sigil.slnx --verify-no-changes --no-restore; echo "format exit=$?"
```

Expected: `YAML OK`, format exit **0**. The second command is exactly what the
workflow's `format` job runs — if it fails here it will fail in CI.

- [ ] **Step 4: Remove the staging directory**

```bash
git rm -r --quiet _agent-setup/
ls _agent-setup 2>&1 || echo "gone"
```

- [ ] **Step 5: Confirm the docs' claims are now true**

```bash
grep -n "CI-enforced\|lint-gated\|pr-guards" AGENTS.md CONTRIBUTING.md
grep -n "\.claude/" CLAUDE.md CONTRIBUTING.md
```

`AGENTS.md:16,96,104` and `CONTRIBUTING.md:69` claim format/PR-title/lockstep
enforcement — now true. `CLAUDE.md` and `CONTRIBUTING.md:59` reference
`.claude/skills/` and `.claude/settings.json` — now present. **No doc edits
should be needed.** If a claim is still false, fix the doc to match reality
rather than leaving it aspirational.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "ci: install pr-guards workflow and .claude config (R20)

pr-guards.yml (conventional-commit PR titles, schema/docs lockstep,
dotnet format) has existed at _agent-setup/github-workflows/ since it was
written but was never copied into .github/workflows/, so AGENTS.md's
'CI-enforced' and CONTRIBUTING.md's 'pr-guards enforces' claims were both
false. Same for .claude/, which CLAUDE.md references.

Installs both and removes the _agent-setup/ staging directory, per its own
apply.ps1 instructions."
```

---

## Task 2b: Widen the CI triggers to cover the RC branch

**Added mid-execution, 2026-07-28**, after Task 2's review found that
`pr-guards.yml:9` scopes itself to `on.pull_request.branches: [main]`.
Investigation showed **all four** workflows do the same, so the RC branch and
every lane PR into it currently run **no CI whatsoever** — no build, no tests,
no coverage gate, no format gate, no gitleaks. The design's premise that
"PR-per-lane gives `pr-guards` a per-lane gate" was false. Human ruled: widen
all four.

This must land **before Task 4**, because Task 4 opens the first lane PR and
performs the G0 gate-proof ceremony, both of which are meaningless without it.

**Files:**
- Modify: `.github/workflows/pr-guards.yml`, `ci.yml`, `docs.yml`, `secret-scan.yml`

**Interfaces:**
- Consumes: Task 2's installed `pr-guards.yml`
- Produces: workflows that trigger for PRs based on `release/**` and for pushes
  to `release/**` — the precondition for every later lane's PR being gated

- [ ] **Step 1: Confirm the scope of the problem**

```bash
grep -n -A3 "^on:" .github/workflows/pr-guards.yml .github/workflows/ci.yml \
  .github/workflows/docs.yml .github/workflows/secret-scan.yml
```

Expected: every one shows `branches: [main]` under `pull_request` (and, for
`ci`/`docs`/`secret-scan`, under `push` too).

- [ ] **Step 2: Widen every branch filter**

In all four files, change each branch list from `[main]` to
`[main, 'release/**']`. Apply it to **both** the `push:` and `pull_request:`
filters wherever each appears. Do not change `types:`, `paths:`, `permissions:`,
`concurrency:`, or any job body — only the branch lists.

Note the resulting behaviour, and confirm you understand it before editing:
lane branches are named `rc/<lane>-<slug>`, which does **not** match
`release/**`. That is intentional. A push to a lane branch triggers nothing; the
`pull_request` filter matches on the **base** branch, so a PR from
`rc/s1-trusted-state` into `release/v0.1.0-alpha` **is** gated, and the `push`
filter re-runs CI on the RC itself after each merge. That is exactly the desired
shape — gate the PR, then re-verify the integrated result.

- [ ] **Step 3: Validate all four still parse**

```bash
for f in pr-guards ci docs secret-scan; do
  python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/$f.yml')); print('$f OK')"
done
```

Expected: four `OK` lines.

- [ ] **Step 4: Verify the filters by inspection**

```bash
grep -n -B1 -A2 "branches:" .github/workflows/*.yml
```

Expected: every `branches:` list now contains both `main` and `release/**`.
Count them — `ci.yml`, `docs.yml`, and `secret-scan.yml` have two each (push +
pull_request), `pr-guards.yml` has one (pull_request only). **Seven** in total.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/
git commit -m "ci: trigger workflows for release/** branches (R20)

All four workflows scoped themselves to branches: [main], so the
release/v0.1.0-alpha RC branch and every lane PR into it would have run no
CI at all — no build, no tests, no coverage gate, no format gate, no
gitleaks. The RC track's premise that a PR per lane gets gated by pr-guards
was false.

Adds release/** to every push and pull_request branch filter. Lane branches
(rc/*) still trigger nothing on push; the pull_request filter matches the
base branch, so lane PRs into the RC are gated and CI re-runs on the RC
after each merge."
```

---

## Task 3: Fix `.gitignore`

**Files:**
- Modify: `.gitignore`

**Interfaces:**
- Consumes: nothing
- Produces: `.superpowers/` reliably excluded from a public repo

- [ ] **Step 1: Confirm both defects**

```bash
grep -n "^\./docs/" .gitignore
git check-ignore -v .superpowers; echo "check-ignore exit=$? (1 = NOT ignored)"
git ls-files .superpowers | wc -l
```

Expected: a hit on `./docs/`; `check-ignore` exit **1**; **0** tracked files.
`.superpowers/` is excluded today only by a nested
`.superpowers/sdd/.gitignore` containing `*` — so the moment a tool writes
`.superpowers/<anything-but-sdd>`, internal agent review diffs become tracked
files in a public repo.

- [ ] **Step 2: Make the edits**

Delete the `./docs/` line. Git anchors any pattern containing a slash to the
`.gitignore`'s directory and never produces a path starting `./`, so it matches
nothing — `docs/` is tracked by accident. It sits in the "Secrets" block, so it
reads as intentional, and a future maintainer "fixing" it to `docs/` would
silently untrack the entire documentation tree.

Then add, under the build-output block:

```gitignore
# Agent scratch space — excluded by repo policy, not by a nested .gitignore
.superpowers/
```

- [ ] **Step 3: Verify both fixes**

```bash
grep -n "^\./docs/" .gitignore || echo "no-op pattern gone"
git check-ignore -v .superpowers; echo "check-ignore exit=$? (0 = ignored)"
git ls-files docs/ | wc -l
```

Expected: pattern gone; `check-ignore` exit **0** citing `.gitignore`; the
`docs/` file count **unchanged and non-zero** (proving you did not accidentally
untrack the docs tree).

- [ ] **Step 4: Commit**

```bash
git add .gitignore
git commit -m "chore: fix no-op gitignore pattern and exclude .superpowers (R40)

'./docs/' matched nothing — git never produces a path starting './', so
docs/ was tracked by accident while the entry read as intentional.

.superpowers/ was excluded only by a nested .gitignore inside itself, so
any tool writing outside .superpowers/sdd/ would have leaked agent review
diffs into a public repo."
```

---

## Task 4: Open the PR and prove the gate works

**Files:** none — this is the gate ceremony.

- [ ] **Step 1: Push and open the lane PR**

```bash
git push -u origin rc/f0-foundation
gh pr create --base release/v0.1.0-alpha --title "chore: stage 0 — format backlog and CI gates (R20, R40, R41)" --body "$(cat <<'EOF'
Stage 0 of the release-candidate track. See docs/plan/release/04-STAGE-0-foundation.md.

- Clears the 28-file dotnet format backlog (R20)
- Installs pr-guards.yml + .claude/ from _agent-setup/, then removes it (R20)
- Fixes the no-op ./docs/ gitignore pattern, excludes .superpowers/ (R40)

Build and test totals unchanged: 1097 tests, 1096 passed, 1 skipped.
EOF
)"
```

- [ ] **Step 2: Watch `pr-guards` pass on this PR**

```bash
gh pr checks --watch
```

Expected: `pr-title`, `schema-lockstep`, and `format` all green. (The PR title
above is a valid conventional commit; `schema-lockstep` no-ops because the
schema is untouched.)

- [ ] **Step 3: Prove the gate can fail — G0's real check**

Open a **throwaway** PR against the RC with a deliberately invalid title.

> **Branch from `rc/f0-foundation`, NOT from the RC.** GitHub runs
> `pull_request` workflows from the PR's **head** branch. `pr-guards.yml` does
> not exist on `release/v0.1.0-alpha` yet (it lands only when this lane merges),
> so a throwaway branched from the RC would run **no `pr-guards` job at all** —
> and would "pass" having proved nothing. That is exactly the vacuous-check
> failure this whole track exists to fix; do not reproduce it here.

```bash
git checkout -b rc/f0-gate-proof rc/f0-foundation
git commit --allow-empty -m "chore: gate proof"
git push -u origin rc/f0-gate-proof
gh pr create --base release/v0.1.0-alpha --title "broken title" --body "Throwaway: proving pr-guards fails a bad title. Close without merging."
gh pr checks --watch || true
```

Expected: the `pr-title` job **RUNS and FAILS** with the conventional-commit
error. **This is the point of the exercise** — a gate nobody has seen fail is
not a gate. If the job does not appear in `gh pr checks` at all, the workflow
was not picked up: stop and report rather than recording a pass.

- [ ] **Step 4: Clean up the throwaway**

```bash
gh pr close --delete-branch $(gh pr list --head rc/f0-gate-proof --json number -q '.[0].number')
git checkout rc/f0-foundation
```

- [ ] **Step 5: Report for gate G0**

State in the PR comment: the format exit code, the build/test totals before and
after, the `check-ignore` exit code, and **the URL of the throwaway PR whose
`pr-title` job failed**. The orchestrator ticks G0 against these.

---

## Lane definition of done

- [ ] `dotnet build Sigil.slnx -c Release` — 0 warnings, 0 errors
- [ ] `dotnet test Sigil.slnx -c Release` — 1097 total, 1096 passed, 1 skipped, unchanged
- [ ] `dotnet format Sigil.slnx --verify-no-changes` — exit 0
- [ ] `git ls-files .claude` non-empty; `_agent-setup/` gone
- [ ] `git check-ignore -v .superpowers` exit 0
- [ ] A throwaway PR with a bad title was observed **failing** `pr-guards`
- [ ] PR open against `release/v0.1.0-alpha`, not merged by you

**Out of scope:** any `src/` or `tests/` change beyond the formatter's own
output; docs rewrites (DOC's lane); deleting stale remote branches (already done
by the orchestrator on 2026-07-28).

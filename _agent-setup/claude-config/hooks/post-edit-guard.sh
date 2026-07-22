#!/usr/bin/env bash
# Advisory post-edit guard for Sigil (never blocks; exit 2 only feeds a
# warning back to the agent — the edit has already happened).
#
# 1. Flags Native-AOT-unsafe patterns in edited .cs files (Debug builds
#    won't catch these; the trim analyzer only runs in Release).
# 2. Reminds about lockstep surfaces when schemas/sigil-schema.json changes.
set -uo pipefail

input=$(cat 2>/dev/null || true)

# Extract tool_input.file_path from the hook JSON without requiring jq.
file=$(printf '%s' "$input" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -1)
[ -z "$file" ] && exit 0

# JSON-escaped Windows paths arrive with doubled backslashes; normalize.
file=$(printf '%s' "$file" | sed 's/\\\\/\//g')

case "$file" in
  *sigil-schema.json)
    cat >&2 <<'EOF'
[sigil guard] schemas/sigil-schema.json changed. Lockstep surfaces (CI enforces):
  - docs/manifest-reference.md
  - examples/** (CI runs `sigil validate` on every example manifest)
  - tests/SigilBuild.Schema.Tests/ fixtures
  - the step-`type` enum is duplicated in MULTIPLE places in the schema — update all
See .claude/skills/schema-change/SKILL.md.
EOF
    exit 2
    ;;
  *.cs)
    [ -f "$file" ] || exit 0
    patterns='Activator\.CreateInstance|Type\.GetType\(|Assembly\.Load|DynamicMethod|MakeGenericType|MakeGenericMethod|Expression\.Lambda|\.Compile\(\)'
    hits=$(grep -nE "$patterns" "$file" 2>/dev/null | grep -v '// *aot-reviewed:' || true)
    if [ -n "$hits" ]; then
      {
        echo "[sigil guard] Possible Native-AOT-unsafe pattern(s) in $file:"
        echo "$hits" | head -10
        echo "Sigil ships AOT-only; these fail at publish time (IL2026/IL3050 are errors in Release)."
        echo "Prefer source generators or the source-generated JSON contexts."
        echo "False positive? Add a '// aot-reviewed: <reason>' comment on the line."
      } >&2
      exit 2
    fi
    ;;
esac
exit 0

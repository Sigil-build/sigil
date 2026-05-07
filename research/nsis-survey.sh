#!/usr/bin/env bash
# nsis-survey.sh - NSIS 5-slot command/plugin-frequency survey
#
# Usage:
#   bash research/nsis-survey.sh <slot-spec> [<slot-spec> ...]
#
# A <slot-spec> is either:
#   - a single .nsi path, in which case the column header is the file's parent dir name
#   - a "label=path1[:path2[:...]]" form, where counts across the colon-separated
#     paths are summed under one column with the given label.
#
# Patterns are the canonical row set defined inline below.
# Counts are static occurrences in .nsi source via `grep -E -o`.
# Re-running with the same args is idempotent.
#
# Regex anchoring: NSIS commands sit at the start of a line (optionally with
# leading whitespace). We anchor most rows with `^\s*` so that occurrences of
# the same word inside comments (`; Use the File command`) or string literals
# (`"WriteRegStr trick"`) don't over-count. Plugin calls of the form
# `nsExec::Exec` / `SimpleSC::*` / `System::Call` etc. are NOT line-anchored,
# because they sometimes appear after macros or labels on the same line; the
# `::` pair is itself enough of a discriminator to avoid false positives.
# LogicLib `${If}` family also goes unanchored — same rationale.
#
# Case-insensitive (`grep -E -i`): NSIS source language is case-insensitive
# (`CreateShortCut` and `CreateShortcut` are the same command), and upstream
# examples mix casings freely — `install-shared.nsi` writes `CreateShortcut`
# while NSIS docs use `CreateShortCut`. We match case-insensitively so the
# count reflects authored usage, not a casing convention. Plugin namespaces
# (`nsExec`, `SimpleSC`, `System`) are also conventionally case-insensitive
# in NSIS source. The line-start path separator (e.g. Windows-style `C:`)
# is NOT touched by the slot-spec parser; pass paths in MSYS form (`/c/...`)
# so the `:` separator in slot-specs doesn't collide with drive letters.

# NOTE: pipefail is intentionally NOT set. Many regex rows have zero matches in
# a given file, which makes `grep -E -o ... | wc -l` exit non-zero — under
# pipefail (combined with `inherit_errexit`), that aborts the count loop
# silently. errexit + nounset still apply.
set -eu

# Each entry is "label|regex". Regexes are POSIX-extended (grep -E).
patterns=(
  'File|^\s*File\b'
  'CreateDirectory|^\s*CreateDirectory\b'
  'Delete|^\s*Delete\b'
  'RmDir|^\s*RmDir\b'
  'WriteRegStr/DWORD/Bin/MultiStr/ExpandStr/None|^\s*Write(Reg(Str|DWORD|Bin|MultiStr|ExpandStr|None))\b'
  'DeleteRegValue|^\s*DeleteRegValue\b'
  'DeleteRegKey|^\s*DeleteRegKey\b'
  'CreateShortCut|^\s*CreateShortCut\b'
  'Exec/ExecWait/ExecShell/nsExec::*|^\s*(Exec|ExecWait|ExecShell)\b|nsExec::(Exec|ExecToLog|ExecToStack)\b'
  'WriteINIStr|^\s*WriteINIStr\b'
  'IfFileExists|^\s*IfFileExists\b'
  '${If}/${ElseIf}/${Else}/${EndIf}|\$\{If\}|\$\{ElseIf\}|\$\{Else\}|\$\{EndIf\}'
  'MessageBox|^\s*MessageBox\b'
  'nsDialogs::Create|nsDialogs::Create\b'
  'SimpleSC::*|SimpleSC::'
  'AccessControl::*|AccessControl::'
  'nsisFirewall/SimpleFC::*|nsisFirewall::|SimpleFC::'
  'InetC::get/NSISdl::download|InetC::(get|head|post)\b|NSISdl::download'
  'System::Call|System::Call\b'
  'Push/Pop|^\s*(Push|Pop)\b'
  'LangString|^\s*LangString\b'
)

# Resolve each <slot-spec> arg into "label" and a list of files.
# Returns parallel arrays via globals: SLOT_LABELS, SLOT_FILES (newline-delimited per index).
parse_slots() {
  SLOT_LABELS=()
  SLOT_FILES=()
  for arg in "$@"; do
    if [[ "$arg" == *"="* ]]; then
      label="${arg%%=*}"
      paths="${arg#*=}"
      # Replace ':' separator with newline
      files="${paths//:/$'\n'}"
    else
      # bare path -> label is the parent-dir basename
      label="$(basename "$(dirname "$arg")")"
      files="$arg"
    fi
    SLOT_LABELS+=("$label")
    SLOT_FILES+=("$files")
  done
}

# Count matches for a given regex across a newline-delimited list of files.
count_matches() {
  local regex="$1"
  local files="$2"
  local total=0
  local f n
  while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    if [[ ! -f "$f" ]]; then
      continue
    fi
    n=$(grep -E -i -o "$regex" "$f" 2>/dev/null | wc -l | tr -d '[:space:]')
    n=${n:-0}
    total=$((total + n))
  done <<< "$files"
  echo "$total"
}

main() {
  if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <slot-spec> [<slot-spec> ...]" >&2
    exit 1
  fi

  parse_slots "$@"

  # Header
  header="| Command / construct |"
  sep="|---|"
  for label in "${SLOT_LABELS[@]}"; do
    header+=" ${label} |"
    sep+="---|"
  done
  header+=" Total |"
  sep+="---|"
  echo "$header"
  echo "$sep"

  # Rows
  for entry in "${patterns[@]}"; do
    label="${entry%%|*}"
    regex="${entry#*|}"
    row="| \`${label}\` |"
    grand=0
    for i in "${!SLOT_LABELS[@]}"; do
      files="${SLOT_FILES[$i]}"
      n=$(count_matches "$regex" "$files")
      row+=" ${n} |"
      grand=$((grand + n))
    done
    row+=" ${grand} |"
    echo "$row"
  done
}

main "$@"

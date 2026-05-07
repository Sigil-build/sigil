#!/usr/bin/env bash
# survey.sh - WiX 5-slot element-frequency survey
#
# Usage:
#   bash research/survey.sh <slot-spec> [<slot-spec> ...]
#
# A <slot-spec> is either:
#   - a single .wxs path, in which case the column header is the file's parent dir name
#   - a "label=path1[:path2[:...]]" form, where counts across the colon-separated
#     paths are summed under one column with the given label.
#
# Patterns are the canonical 19-row set defined inline below.
# Counts are static occurrences in .wxs source via `grep -E -o`.
# Re-running with the same args is idempotent.

set -euo pipefail

# Each entry is "label|regex". Regexes are POSIX-extended (grep -E).
patterns=(
  '<File>|<File\b'
  '<Component>|<Component\b'
  '<RegistryValue>|<RegistryValue\b'
  '<RegistryKey>|<RegistryKey\b'
  '<Shortcut>|<Shortcut\b'
  '<Environment>|<Environment\b'
  '<ServiceInstall>|<ServiceInstall\b'
  '<ServiceControl>|<ServiceControl\b'
  '<CustomAction> Type 50/226 (exec)|<CustomAction\b[^>]*Type=.(50|226).'
  '<CustomAction> Type 1/17 (DLL)|<CustomAction\b[^>]*Type=.(1|17).'
  '<RemoveFile>|<RemoveFile\b'
  '<RemoveFolder>|<RemoveFolder\b'
  '<MoveFile>|<MoveFile\b'
  '<IniFile>|<IniFile\b'
  '<ProgId>/<Extension>/<Verb>|<(ProgId|Extension|Verb)\b'
  'util:PermissionEx|<util:PermissionEx\b|<PermissionEx\b'
  'fw:FirewallException|<fire:FirewallException\b|<firewall:FirewallException\b|<fw:FirewallException\b'
  'difx:DriverPackage|<difx:DriverPackage\b'
  'Conditional|<Condition\b|Condition='
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
    n=$(grep -E -o "$regex" "$f" 2>/dev/null | wc -l | tr -d '[:space:]')
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
  header="| Element / construct |"
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

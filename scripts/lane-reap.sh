#!/usr/bin/env bash
# Automatic merged-lane reaper for both Mac (/Volumes/Files/assembly-worktrees) and Workbox
# (~/dev/work/worktrees/Assembly). Safe to run periodically from systemd/launchd or before `lane.sh new`:
#   - Only inspects managed lane worktrees containing a `.lane` file under the lanes root.
#   - Refuses dirty worktrees, unmerged branches, or lanes with live processes (delegated to `lane.sh remove`).
#   - Prunes stale git worktree metadata afterward.
#
# Usage: scripts/lane-reap.sh [--no-fetch] [--dry-run] [--quiet]
set -euo pipefail
cd "$(dirname "$0")/.."
script_root=$PWD

fetch=1
dry_run=0
quiet=0
for arg in "$@"; do
  case "$arg" in
    --no-fetch) fetch=0 ;;
    --dry-run) dry_run=1 ;;
    --quiet) quiet=1 ;;
    *) echo "usage: scripts/lane-reap.sh [--no-fetch] [--dry-run] [--quiet]" >&2; exit 2 ;;
  esac
done

log() {
  [ "$quiet" = 1 ] || echo "lane-reap: $*"
}

if [ -n "${ASSEMBLY_WORKTREES_DIR:-}" ]; then
  lanes=$ASSEMBLY_WORKTREES_DIR
elif [ "$(uname -s)" = Darwin ] && [[ "$HOME" == /Users/* ]] && [ -d /Volumes/Files ]; then
  lanes=/Volumes/Files/assembly-worktrees
else
  lanes=$HOME/dev/work/worktrees/Assembly
fi
legacy_lanes=$HOME/dev/work/worktrees/Assembly
main=$(dirname "$(git rev-parse --path-format=absolute --git-common-dir)")

if [ "$fetch" = 1 ]; then
  git -C "$main" fetch -q origin main 2>/dev/null || true
fi

is_merged() {
  local dir=$1
  git -C "$dir" merge-base --is-ancestor HEAD main 2>/dev/null && return 0
  git -C "$dir" rev-parse -q --verify refs/remotes/origin/main >/dev/null 2>&1 \
    && git -C "$dir" merge-base --is-ancestor HEAD origin/main 2>/dev/null && return 0
  return 1
}

reaped=0
skipped=0
while IFS= read -r line; do
  case "$line" in
    "worktree "*) dir=${line#worktree } ;;
    *) continue ;;
  esac
  # Only consider managed lanes inside the configured lanes directory (or legacy Mac lanes folder).
  case "$dir" in
    "$lanes/"* | "$legacy_lanes/"*) ;;
    *) continue ;;
  esac
  [ -f "$dir/.lane" ] || continue
  name=$(basename "$dir")

  if [ -n "$(git -C "$dir" status --porcelain 2>/dev/null || echo dirty)" ]; then
    log "keep $name (uncommitted changes)"
    skipped=$((skipped + 1))
    continue
  fi
  if ! is_merged "$dir"; then
    log "keep $name (not merged into main)"
    skipped=$((skipped + 1))
    continue
  fi

  if [ "$dry_run" = 1 ]; then
    log "would reap $name ($dir)"
    reaped=$((reaped + 1))
    continue
  fi

  if out=$(bash "$script_root/scripts/lane.sh" remove "$name" 2>&1); then
    log "reaped merged lane $name"
    reaped=$((reaped + 1))
  else
    log "keep $name ($out)"
    skipped=$((skipped + 1))
  fi
done < <(git -C "$main" worktree list --porcelain)

if [ "$dry_run" = 0 ]; then
  git -C "$main" worktree prune
fi
log "done (reaped=$reaped, kept=$skipped)"

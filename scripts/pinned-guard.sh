#!/usr/bin/env bash
# Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit/NotebookEdit): a new pinned test may be
# written, but an existing one may not be changed. Jesse's OK goes in .pinned-approvals (scripts/check-pinned.sh).
# It fails closed: without jq, or with input it can't read, every edit is refused.
command -v jq >/dev/null || { echo "pinned-guard: jq isn't installed, so no edit can be checked; refusing" >&2; exit 2; }
path=$(jq -er '.tool_input.file_path // .tool_input.notebook_path') \
  || { echo "pinned-guard: couldn't read the edit's file path; refusing" >&2; exit 2; }
case "$path" in
  */Assembly.Tests/Pinned/*|*/app/test/pinned/*)
    if [ -e "$path" ]; then
      echo "$path is a pinned test: changing it needs Jesse's OK (AGENTS.md). Stop and ask him." >&2
      exit 2
    fi ;;
esac
exit 0

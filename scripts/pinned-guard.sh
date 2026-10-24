#!/usr/bin/env bash
# Claude Code hook (.claude/settings.json, before Edit/Write/MultiEdit/NotebookEdit): a new pinned test may be
# written, but an existing one may not be changed. Jesse's OK goes in .pinned-approvals (scripts/check-pinned.sh).
path=$(jq -r '.tool_input.file_path // .tool_input.notebook_path // empty')
case "$path" in
  */Assembly.Tests/Pinned/*|*/app/test/pinned/*)
    if [ -e "$path" ]; then
      echo "$path is a pinned test: changing it needs Jesse's OK (AGENTS.md). Stop and ask him." >&2
      exit 2
    fi ;;
esac
exit 0

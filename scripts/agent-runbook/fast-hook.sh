#!/bin/sh
# Read only hook stdin. No eval, session lookup, or provider configuration reads.
umask 077
payload=$(cat)
case "$1" in
  pre)
    case "$payload" in *worktree*) ;; *) printf '{}\n'; exit 0 ;; esac
    ;;
  post)
    transcript=$(printf '%s' "$payload" | sed -n 's/.*"transcript_path"[[:space:]]*:[[:space:]]*"\([^"\\]*\)".*/\1/p')
    [ -f "$transcript" ] || { printf '{}\n'; exit 0; }
    # The timestamp belongs to this transcript, so unrelated sessions do not collide.
    key=$(printf '%s' "$transcript" | cksum | cut -d ' ' -f 1)
    dir="$HOME/.local/state/agent-runbook/fast"
    mkdir -p "$dir" || exit 0
    stamp=$(wc -c < "$transcript" | tr -d ' ')
    previous=''
    [ ! -f "$dir/$key" ] || read -r previous < "$dir/$key"
    [ "$stamp" != "$previous" ] || { printf '{}\n'; exit 0; }
    # Node owns atomic state writes, including this prefilter's growth stamp.
    export RUNBOOK_FAST_STAMP="$dir/$key" RUNBOOK_FAST_BYTES="$stamp"
    ;;
esac
printf '%s' "$payload" | "$2" "$3" hook "$4"

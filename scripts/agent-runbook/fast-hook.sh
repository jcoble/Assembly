#!/bin/sh
# Read only hook stdin. No eval, session lookup, or provider configuration reads.
umask 077
payload=$(cat)
case "$1" in
  pre)
    # Pass anything that could name the live data folder: its name, any backslash (JSON or shell escapes),
    # or a glob character beside a home-directory spelling.
    case "$payload" in
      *worktree*|*[Aa][Ss][Ss][Ee][Mm][Bb][Ll][Yy]-[Dd][Oo][Tt][Nn][Ee][Tt]*|*\\*) ;;
      *'*'*|*'?'*|*'['*)
        case "$payload" in *'~'*|*HOME*|*/Users/*|*/home/*) ;; *) printf '{}\n'; exit 0 ;; esac ;;
      *) printf '{}\n'; exit 0 ;;
    esac
    ;;
  post)
    # Standard JSON paths/IDs need no parser process. Escaped paths go to Node.
    value=${payload#*\"transcript_path\"}; value=${value#*:}; value=${value#*\"}
    transcript=${value%%\"*}
    value=${payload#*\"session_id\"}; value=${value#*:}; value=${value#*\"}
    key=${value%%\"*}
    case "$payload" in
      *\"agent_id\"*) value=${payload#*\"agent_id\"}; value=${value#*:}; value=${value#*\"}; key=${value%%\"*} ;;
    esac
    case "$key:$transcript" in
      *\\*|:*) ;; # Let Node parse any escaped or unrecognized input.
      *)
        case "$key" in *[!a-zA-Z0-9_-]*) ;; *)
          if [ -f "$transcript" ]; then
            stamp=$(wc -c < "$transcript")
            # Strip wc's leading spaces using shell expansion.
            stamp=${stamp##* }
            file="$HOME/.local/state/agent-runbook/fast/$4/$key"
            previous=''
            [ ! -f "$file" ] || read -r previous < "$file"
            [ "$stamp:$transcript" != "$previous" ] || { printf '{}\n'; exit 0; }
            export RUNBOOK_FAST_STAMP="$file" RUNBOOK_FAST_BYTES="$stamp:$transcript"
          fi
        ;; esac
      ;;
    esac
    ;;
esac
printf '%s' "$payload" | "$2" "$3" hook "$4"

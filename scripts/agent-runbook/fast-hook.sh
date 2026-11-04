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
esac
printf '%s' "$payload" | "$2" "$3" hook "$4"

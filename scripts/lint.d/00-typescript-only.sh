#!/usr/bin/env bash
# TypeScript only (AGENTS.md, Code): fails, naming each one, if the repo tracks any .js, .mjs or .cjs file.
cd "$(dirname "$0")/../.."
files=$(git ls-files -- '*.js' '*.mjs' '*.cjs')
[ -z "$files" ] && { echo "typescript only: no .js, .mjs or .cjs files"; exit 0; }
while IFS= read -r f; do echo "typescript only: $f is JavaScript; make it a .ts file run by node directly" >&2; done <<<"$files"
exit 1

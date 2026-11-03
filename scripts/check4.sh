#!/usr/bin/env bash
# The four-way check: one turn each for Claude Code and Codex, on this checkout's local backend and on its Workbox
# Test host, against this commit. It needs both running from this commit: the local backend on DESKTOP_PORT (the window,
# `pnpm app:dev`, or the headless backend without --remote on the workbox) and the host
# (`dotnet/scripts/workbox.sh serve`). Evidence goes to evidence/<commit>/four-way/: per case its rows and the
# agent's transcript path and size, and summary.json and summary.md. Commit that folder; a code commit pushed
# to main without a passing summary for it or a later commit fails CI (scripts/check-evidence.sh).
#   scripts/check4.sh   runs the four turns, and every feature scenario (scripts/features/*.sh, see the README
#                       there) in each case while the turn streams; exits 0 only if all four cases passed, each with
#                       its turn and every scenario
#   scripts/check4.sh --feature <name>...   the same with only those scenarios, for quick runs while working; it
#                       writes to evidence/<commit>/four-way-partial/, with "partial": true, and isn't evidence
#   scripts/check4.sh window <commit> <file>...   records the window screenshots' names in that commit's summary;
#                       the files themselves stay in the project's private files, not in this repo
set -euo pipefail
cd "$(dirname "$0")/.."
. scripts/lane-env.sh

if [ "${1:-}" = window ]; then
  commit=${2:?usage: scripts/check4.sh window <commit> <file>...}; shift 2
  [ $# -gt 0 ] || { echo "usage: scripts/check4.sh window <commit> <file>..." >&2; exit 2; }
  summary=evidence/$commit/four-way/summary.json
  [ -f "$summary" ] || { echo "no summary for $commit; run scripts/check4.sh on it first" >&2; exit 1; }
  lane=$(jq -r .lane "$summary")
  jq --arg where "project files: notes/evidence/four-way/$lane/${commit:0:7}/window/" --args \
    '.windowScreenshots = {where: $where, files: $ARGS.positional}' "$@" < "$summary" > "$summary.new"
  jq -e .commit "$summary.new" >/dev/null || { rm -f "$summary.new"; echo "couldn't update $summary" >&2; exit 1; }
  mv "$summary.new" "$summary"
  sed -i.bak '/^Window screenshots:/d' "${summary%.json}.md" && rm -f "${summary%.json}.md.bak"
  jq -r '"Window screenshots: \(.windowScreenshots.files | join(", ")) (\(.windowScreenshots.where))"' "$summary" | tee -a "${summary%.json}.md"
  exit
fi

features=()
while [ $# -gt 0 ]; do
  case "$1" in
    --feature) features+=("${2:?--feature needs a name}"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
partial=true
if [ ${#features[@]} = 0 ]; then
  partial=false
  for f in scripts/features/*.sh; do [ -e "$f" ] && features+=("$(basename "$f" .sh)"); done
fi
for f in ${features[@]+"${features[@]}"}; do
  [ -x "scripts/features/$f.sh" ] || { echo "no scenario scripts/features/$f.sh (or it isn't executable)" >&2; exit 2; }
done
commit=$(git rev-parse HEAD)
out=evidence/$commit/four-way; [ "$partial" = true ] && out+=-partial
# Keep every previous attempt, including failures, before creating the current receipt.
if [ -e "$out" ]; then
  archive="$out.attempt-$(date -u +%Y%m%dT%H%M%S)-$$"
  mv "$out" "$archive"
  echo "previous attempt: $archive"
fi
mkdir -p "$out"

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
started=$(now)
local_base=http://127.0.0.1:$DESKTOP_PORT
box_base=""
forward_pid=""
worker_pid=""
stop_forward() {
  if [ -n "$worker_pid" ]; then kill "$worker_pid" 2>/dev/null || true; wait "$worker_pid" 2>/dev/null || true; fi
  if [ -n "$forward_pid" ]; then kill "$forward_pid" 2>/dev/null || true; wait "$forward_pid" 2>/dev/null || true; fi
}
trap stop_forward EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
# Tokens stay in variables and reach socket hello through the environment, never argv, URLs or logs.
local_token=$(sed -n 's/^ASSEMBLY_TOKEN=//p' "$TOKEN_FILE" 2>/dev/null || true)
box_token=$(ssh workbox-test "sed -n 's/^ASSEMBLY_TOKEN=//p' ~/$BOX_TOKEN_FILE 2>/dev/null" || true)

# Preflight: both backends answer and run this commit. If not, every case fails with the reason.
blocked=""
[ -z "$(git status --porcelain -- . ':!evidence')" ] || blocked="the checkout has uncommitted changes, so this isn't a check of $commit"
if [ -z "$blocked" ]; then
  setup=$(CHECK4_API=$local_base CHECK4_TOKEN=$local_token CHECK4_BOX_PORT=$BOX_PORT CHECK4_BOX_TOKEN_FILE=$BOX_TOKEN_FILE \
    node scripts/features/machines.ts setup 2>&1) || blocked=$setup
  printf '%s\n' "$setup" > "$out/machines-setup.log"
fi
# Each waits up to 30 s for the backend just started to answer with this commit's code, so one still starting, or an
# old process not yet stopped, isn't taken for it.
local_commit="" box_commit=""
if [ -z "$blocked" ]; then
  local_commit=$(echo "$local_token" | scripts/wait-version.sh "the local backend" "$local_base" "$commit") || { blocked=$local_commit; local_commit=""; }
fi
if [ -z "$blocked" ]; then
  forward_port=$(node scripts/features/machines.ts port)
  box_base=http://127.0.0.1:$forward_port
  ssh -N -n -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 \
    -L "127.0.0.1:$forward_port:127.0.0.1:$BOX_PORT" workbox-test > "$out/forward.log" 2>&1 &
  forward_pid=$!
  box_commit=$(echo "$box_token" | scripts/wait-version.sh "the Workbox Test host (through the tunnel)" "$box_base" "$commit") || { blocked=$box_commit; box_commit=""; }
fi

# A tiny repo for each session to check out, on each machine.
repo_local=$DATA/fixtures/notes-repo
repo_box=$(ssh workbox-test 'echo $HOME')/$BOX_DATA/fixtures/notes-repo
make_repo='[ -d "$1/.git" ] || { mkdir -p "$1" && cd "$1" && git init -q && echo "notes" > README && git add README && git -c user.name=check4 -c user.email=check4@example.invalid commit -qm notes; }'
bash -c "$make_repo" _ "$repo_local"
ssh workbox-test "bash -c '$make_repo' _ '$repo_box'"
host_local=$(hostname)
host_box=$(ssh workbox-test hostname)

transcript() { # transcript agent session-id local|box -> "path<TAB>size"
  local find
  case "$1" in
    claude) find="find ~/.claude/projects -name '$2.jsonl'" ;;
    *) find="find ~/.codex/sessions -name '*$2*.jsonl'" ;;
  esac
  local cmd="p=\$($find 2>/dev/null | head -1); [ -n \"\$p\" ] && printf '%s\t%s' \"\$p\" \"\$(wc -c < \"\$p\" | tr -d ' ')\""
  if [ "$3" = box ]; then ssh workbox-test "$cmd" || true; else bash -c "$cmd" || true; fi
}

cases=()
for agent in claude codex; do
  for where in local box; do
    name=$agent-$([ $where = local ] && echo local || echo workbox-test)
    case_started=$(now) session="" run="" state="" reply="" sid="" tpath="" tsize=0 reason="" failed_feature=""
    feature_results=()
    if [ -n "$blocked" ]; then
      reason=$blocked
    else
      repo=$repo_local host=$host_local host_base=$local_base host_token=$local_token host_commit=$local_commit
      [ $where = box ] && repo=$repo_box host=$host_box host_base=$box_base host_token=$box_token host_commit=$box_commit
      worker_code=0
      CHECK4_API=$local_base CHECK4_TOKEN=$local_token CHECK4_AGENT=$agent CHECK4_MACHINE=${name#*-} \
        CHECK4_REPO=$repo CHECK4_HOST_API=$host_base CHECK4_HOST_TOKEN=$host_token CHECK4_HOST_NAME=$host \
        CHECK4_COMMIT=$local_commit CHECK4_HOST_COMMIT=$host_commit CHECK4_CASE=$name CHECK4_OUT=$out \
        CHECK4_FEATURES="$(printf '%s\n' ${features[@]+"${features[@]}"} | jq -R 'select(length > 0)' | jq -s .)" \
        node scripts/check4-case.ts > "$out/$name.case.json" 2> "$out/$name.case.log" &
      worker_pid=$!
      wait "$worker_pid" || worker_code=$?
      worker_pid=""
      receipt=$out/$name.case.json
      if ! jq -e 'type == "object" and (.features | type == "array") and (.reason | type == "string")' "$receipt" >/dev/null 2>&1; then
        reason="the socket case worker exited $worker_code without a valid receipt (see $name.case.log)"
      else
        session=$(jq -r '.session' "$receipt")
        run=$(jq -r '.run' "$receipt")
        state=$(jq -r '.state' "$receipt")
        reply=$(jq -r '.reply' "$receipt")
        sid=$(jq -r '.acpSessionId // empty' "$receipt")
        reason=$(jq -r '.reason' "$receipt")
        while IFS= read -r feature_result; do feature_results+=("$feature_result"); done < <(jq -c '.features[]' "$receipt")
        if [ -z "$reason" ] && [ "$worker_code" != 0 ]; then reason="the socket case worker exited $worker_code"; fi
        [ -n "$sid" ] && IFS=$'\t' read -r tpath tsize < <(transcript "$agent" "$sid" "$where"; echo) || true
        if [ -z "$reason" ] && [ -z "$tpath" ]; then reason="no $agent transcript found for session ${sid:-unknown}"; fi
      fi
    fi
    pass=$([ -z "$reason" ] && echo true || echo false)
    echo "$name: $([ "$pass" = true ] && echo pass || echo "FAIL - $reason")"
    cases+=("$(jq -cn --arg agent "$agent" --arg machine "${name#*-}" --arg session "$session" --arg run "$run" \
      --arg started "$case_started" --arg finished "$(now)" --arg state "$state" --arg reply "$reply" \
      --arg acp "$sid" --arg path "$tpath" --argjson size "${tsize:-0}" --argjson pass "$pass" --arg reason "$reason" \
      --argjson scenarios "$(printf '%s\n' ${feature_results[@]+"${feature_results[@]}"} | jq -s .)" \
      '{agent: $agent, machine: $machine, sessionId: $session, runId: $run, startedAt: $started, finishedAt: $finished,
        state: $state, reply: $reply, acpSessionId: $acp, transcript: {path: $path, bytes: $size},
        rows: "\($agent)-\($machine).rows.json", features: $scenarios, pass: $pass, reason: $reason}')")
  done
done

printf '%s\n' "${cases[@]}" | jq -s --arg commit "$commit" --arg lane "${LANE_NAME:-main}" --arg host "$host_local" \
  --arg started "$started" --arg finished "$(now)" \
  --argjson features "$(printf '%s\n' ${features[@]+"${features[@]}"} | jq -R 'select(length > 0)' | jq -s .)" \
  --arg localBuilt "$local_commit" --arg boxBuilt "$box_commit" --argjson partial "$partial" \
  '{commit: $commit, lane: $lane, localMachine: $host, startedAt: $started, finishedAt: $finished,
    builtFrom: {local: $localBuilt, workboxTest: $boxBuilt}, features: $features, partial: $partial,
    pass: (length == 4 and all(.pass)), cases: ., windowScreenshots: "missing"}' > "$out/summary.json"
jq -r '"# Four-way check of \(.commit)\n\nLane \(.lane), local machine \(.localMachine), \(.startedAt) to \(.finishedAt): \(if .pass then "all four passed" else "FAILED" end).\nFeature scenarios: \(if (.features | length) > 0 then .features | join(", ") else "none" end)\(if .partial then " (partial run, not evidence)" else "" end).\n",
  (.cases[] | "- \(.agent) on \(.machine): \(if .pass then "pass" else "FAIL: \(.reason)" end) (session \(.sessionId), transcript \(.transcript.path) \(.transcript.bytes) bytes)"),
  "\nWindow screenshots: missing"' "$out/summary.json" > "$out/summary.md"
echo "summary: $out/summary.json"
jq -e .pass "$out/summary.json" >/dev/null

#!/usr/bin/env bash
# The four-way check: one turn each for Claude Code and Codex, on this checkout's local backend and on its Workbox
# Test host, against this commit. It needs both running from this commit: the local backend on MAC_PORT (the window,
# `shell/assembly.sh start` with ASSEMBLY_REMOTE=workbox-test, or the headless backend on the workbox) and the host
# (`dotnet/scripts/workbox.sh serve`). Evidence goes to evidence/<commit>/four-way/: per case its rows and the
# agent's transcript path and size, and summary.json and summary.md. Commit that folder; a feature commit pushed
# to main without a passing summary for it or a later commit fails CI (scripts/check-evidence.sh).
#   scripts/check4.sh [--feature <name>]...   runs the four turns, and with --feature that feature's scenario
#                       (scripts/features/<name>.sh, see its README) in each case while the turn streams; exits 0
#                       only if all four cases passed, each with its turn and every scenario
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
for f in ${features[@]+"${features[@]}"}; do
  [ -x "scripts/features/$f.sh" ] || { echo "no scenario scripts/features/$f.sh (or it isn't executable)" >&2; exit 2; }
done
commit=$(git rev-parse HEAD)
out=evidence/$commit/four-way
rm -rf "$out"
mkdir -p "$out"

now() { date -u +%Y-%m-%dT%H:%M:%SZ; }
started=$(now)
local_base=http://127.0.0.1:$MAC_PORT
box_base=http://127.0.0.1:$((BOX_PORT + 10000))   # the local backend's tunnel to the host
# Tokens stay in variables and reach curl on stdin, never in argv, a file or the log.
local_token=$(sed -n 's/^ASSEMBLY_TOKEN=//p' "$TOKEN_FILE" 2>/dev/null || true)
box_token=$(ssh workbox-test "sed -n 's/^ASSEMBLY_TOKEN=//p' ~/$BOX_TOKEN_FILE 2>/dev/null" || true)
api() { # api local|box METHOD PATH [JSON]
  local token=$local_token base=$local_base
  [ "$1" = box ] && token=$box_token base=$box_base
  printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$token" \
    | curl -s --max-time 30 -H @- -X "$2" "$base$3" ${4:+-d "$4"}
}

# Preflight: both backends answer and run this commit. If not, every case fails with the reason.
blocked=""
[ -z "$(git status --porcelain -- . ':!evidence')" ] || blocked="the checkout has uncommitted changes, so this isn't a check of $commit"
local_commit=$(api local GET /version | jq -r '.commit // empty' 2>/dev/null || true)
box_commit=$(api box GET /version | jq -r '.commit // empty' 2>/dev/null || true)
# A backend built from an earlier commit counts when the code between the two is the same (only scripts or docs moved).
same_code() { [ "$1" = "$commit" ] || { git cat-file -e "$1^{commit}" 2>/dev/null && git diff --quiet "$1" "$commit" -- dotnet app shell; }; }
if [ -z "$blocked" ]; then
  if [ -z "$local_commit" ]; then blocked="the local backend on $local_base didn't answer /version"
  elif ! same_code "$local_commit"; then blocked="the local backend runs $local_commit, not the code of $commit"
  elif [ -z "$box_commit" ]; then blocked="the Workbox Test host (through $box_base) didn't answer /version"
  elif ! same_code "$box_commit"; then blocked="the Workbox Test host runs $box_commit, not the code of $commit"
  fi
fi

# A tiny repo for each thread to check out, on each machine.
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
    case_started=$(now) thread="" run="" state="" reply="" sid="" tpath="" tsize=0 reason="" failed_feature=""
    feature_results=()
    if [ -n "$blocked" ]; then
      reason=$blocked
    else
      repo=$repo_local machine_json=""; host=$host_local
      [ $where = box ] && repo=$repo_box machine_json=',"machine":"workbox-test"' host=$host_box
      thread=$(api local POST /threads "{\"agent\":\"$agent\",\"repoUrl\":\"$repo\"$machine_json}" | jq -r '.threadId // empty' 2>/dev/null || true)
      prompt="Run the shell command hostname, then reply with only its output and, on the next line, the word four-way-ok."
      [ -n "$thread" ] && run=$(api local POST "/threads/$thread/send" "$(jq -cn --arg t "$prompt" '{text: $t}')" | jq -r '.runId // empty' 2>/dev/null || true)
      if [ -z "$thread" ]; then reason="the thread wasn't created"
      elif [ -z "$run" ]; then reason="the turn didn't start"
      else
        # Each feature scenario runs against this case's thread while its turn streams.
        pids=()
        for f in ${features[@]+"${features[@]}"}; do
          CHECK4_API=$local_base CHECK4_TOKEN=$local_token CHECK4_THREAD=$thread CHECK4_RUN=$run CHECK4_AGENT=$agent \
            CHECK4_MACHINE=${name#*-} CHECK4_REPO=$repo "scripts/features/$f.sh" > "$out/$name.$f.log" 2>&1 &
          pids+=($!)
        done
        for _ in $(seq 120); do
          rows=$(api local GET "/threads/$thread/rows?limit=500" || true)
          state=$(jq -r --arg r "$run" '[.rows[] | select(.runId == $r and .kind == "turn" and .payload.state != "started")][-1].payload.state // empty' <<<"$rows" 2>/dev/null || true)
          [ -n "$state" ] && [ "$(jq -r '.activeRunId // empty' <<<"$rows")" = "" ] && break
          sleep 2
        done
        jq --arg r "$run" '[.rows[] | select(.runId == $r)]' <<<"$rows" > "$out/$name.rows.json"
        reply=$(jq -r '[.[] | select(.kind == "message") | .payload | .. | objects | .value? | strings] | join("\n")' "$out/$name.rows.json")
        sid=$(api "$where" GET "/threads/$thread" | jq -r '.acpSessionId // empty' 2>/dev/null || true)
        [ -n "$sid" ] && IFS=$'\t' read -r tpath tsize < <(transcript "$agent" "$sid" "$where"; echo) || true
        feature_results=()
        i=0
        for f in ${features[@]+"${features[@]}"}; do
          code=0; wait "${pids[$i]}" || code=$?; i=$((i + 1))
          feature_results+=("$(jq -cn --arg name "$f" --argjson code "$code" --rawfile log "$out/$name.$f.log" \
            '{name: $name, pass: ($code == 0), exitCode: $code, steps: ($log | split("\n") | map(select(length > 0)))}')")
          [ "$code" = 0 ] || failed_feature=${failed_feature:-$f}
        done
        if [ "$state" != finished ]; then reason="the turn ended ${state:-never (timed out after 240 s)}"
        elif ! grep -qF "$host" <<<"$reply"; then reason="the reply doesn't show this machine's hostname ($host)"
        elif ! grep -qF four-way-ok <<<"$reply"; then reason="the reply doesn't end with four-way-ok"
        elif [ -z "$tpath" ]; then reason="no $agent transcript found for session ${sid:-unknown}"
        elif [ -n "${failed_feature:-}" ]; then reason="the $failed_feature scenario failed (see $name.$failed_feature.log)"
        fi
      fi
    fi
    pass=$([ -z "$reason" ] && echo true || echo false)
    echo "$name: $([ "$pass" = true ] && echo pass || echo "FAIL - $reason")"
    cases+=("$(jq -cn --arg agent "$agent" --arg machine "${name#*-}" --arg thread "$thread" --arg run "$run" \
      --arg started "$case_started" --arg finished "$(now)" --arg state "$state" --arg reply "$reply" \
      --arg session "$sid" --arg path "$tpath" --argjson size "${tsize:-0}" --argjson pass "$pass" --arg reason "$reason" \
      --argjson scenarios "$(printf '%s\n' ${feature_results[@]+"${feature_results[@]}"} | jq -s .)" \
      '{agent: $agent, machine: $machine, threadId: $thread, runId: $run, startedAt: $started, finishedAt: $finished,
        state: $state, reply: $reply, acpSessionId: $session, transcript: {path: $path, bytes: $size},
        rows: "\($agent)-\($machine).rows.json", features: $scenarios, pass: $pass, reason: $reason}')")
  done
done

printf '%s\n' "${cases[@]}" | jq -s --arg commit "$commit" --arg lane "${LANE_NAME:-main}" --arg host "$host_local" \
  --arg started "$started" --arg finished "$(now)" \
  --argjson features "$(printf '%s\n' ${features[@]+"${features[@]}"} | jq -R 'select(length > 0)' | jq -s .)" \
  --arg localBuilt "$local_commit" --arg boxBuilt "$box_commit" \
  '{commit: $commit, lane: $lane, localMachine: $host, startedAt: $started, finishedAt: $finished,
    builtFrom: {local: $localBuilt, workboxTest: $boxBuilt}, features: $features,
    pass: (length == 4 and all(.pass)), cases: ., windowScreenshots: "missing"}' > "$out/summary.json"
jq -r '"# Four-way check of \(.commit)\n\nLane \(.lane), local machine \(.localMachine), \(.startedAt) to \(.finishedAt): \(if .pass then "all four passed" else "FAILED" end).\nFeature scenarios: \(if (.features | length) > 0 then .features | join(", ") else "none" end).\n",
  (.cases[] | "- \(.agent) on \(.machine): \(if .pass then "pass" else "FAIL: \(.reason)" end) (thread \(.threadId), transcript \(.transcript.path) \(.transcript.bytes) bytes)"),
  "\nWindow screenshots: missing"' "$out/summary.json" > "$out/summary.md"
echo "summary: $out/summary.json"
jq -e .pass "$out/summary.json" >/dev/null

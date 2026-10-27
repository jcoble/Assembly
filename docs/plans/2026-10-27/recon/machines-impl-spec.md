Recon, read-only, 2026-10-27, not re-verified unless marked

# Build: the `machines` topic (plan C1, "Next after 1c")

Worktree: `/home/blackcolours/dev/work/worktrees/Assembly/base-machines`, branch `lane/base-machines`. Don't change branch, and don't push.

## Sources, in order of authority
1. **The plan note:** `docs/plans/2026-10-26/implementation-plan.md`, the bullet "Next after 1c: the `machines` topic". This is the contract; read it first.
2. **The recon spec:** `/tmp/claude-1000/-home-blackcolours-dev-work-Assembly--claude-worktrees-bridge-cse-01ShsqM88b2iaR9tvjEdVUp9/d980aa35-ac84-5b12-a0c4-0748904d3d99/scratchpad/recon-machines-spec.md`. It has file:line edit points, the registration pattern and the test harness. It was written before the note's last two revisions, so the note wins wherever they differ. Known differences:
   - **No `version` and no `host`** in the item. The item is `{name, kind, reachable, reason}`. Drop every spec test assertion on `version`.
   - **`reason`** is set while `reachable` is false, and null otherwise. Its values: "not checked yet" at the start, "didn't answer", "ssh tunnel exited", "the remote refused this computer's token", or the format message (for example "stores rows in format 99; this computer reads 4", built from `RemoteMachine.cs:186`'s text).
   - **Non-2xx:** `ReadJsonAsync` throws Application `CallFailed`, giving a `failed` frame, except 401 and 403. Those throw `MachineUnreachable` with the token reason and record reachable=false.
   - **The probe:**
     - The first one goes on the first clock tick after a `machines` subscribe, then every 15th tick.
     - Only remotes with no live follower link are probed.
     - Each probe has its own 5 s timeout, a `CancellationTokenSource` with `CancelAfter`, not `HttpClient`'s 30 s.
     - A remote's probe never starts while its last one is still in flight.
     - When the last `machines` subscriber leaves, the probe in flight is cancelled.
   - **The clock** runs while `clock` or `machines` has a subscriber, and still stops when neither has one.
   - **A format mismatch** records reachable=false once, never true first, so there's no flap.

## Steps
1. **Red tests first, in one commit.** Add the tests listed in the note's "Tests (red first)" bullet, using the spec's section 4 harness, with the differences above. Add stubs only where the build needs them to compile, such as empty members that throw `NotImplementedException` or do nothing, so the commit hook's build passes and the tests fail at run time for the right reason. Run the new tests and confirm they fail. Commit message, title: `Machines tests: the machines topic, red`.
2. **The code, in one or more commits,** until the filter below and the full suite pass.
3. **Stop and report** if production code heads past about 400 lines, or a new file the note doesn't name seems necessary. Send a one-paragraph proposal instead of building it.

## Verification (exact)
- **Before each build:** run `free -g`, and don't start one if 6 GB or less is available. Run one build at a time, always with `MSBUILDDISABLENODEREUSE=1`.
- **Filter:** `cd /home/blackcolours/dev/work/worktrees/Assembly/base-machines && MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/Assembly.Tests --filter "FullyQualifiedName~MachinesSocketTests|FullyQualifiedName~RemoteMachineTests|FullyQualifiedName~RemoteTunnelTests|FullyQualifiedName~ChatSocketTests|FullyQualifiedName~ClockSocketTests|FullyQualifiedName~ArchitectureTests"`
- **Full suite:** `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/Assembly.Tests`. Every test must pass.
- **Flakiness:** run the new socket tests 5 times in a row. All 5 must pass.

## Hard rules
- **KISS and YAGNI.** Deliver the smallest diff that makes the named tests pass. Unless the note asks for it, adding any of these is a defect: a new interface, abstraction or base class; a config option; a compatibility shim; a factory; or speculative generality. No drive-by refactors.
- **Commit messages** end with the two lines `Architecture: unchanged` and `Committed-by: Claude`. No Co-Authored-By, no model names. Plain English.
- **Git:**
  - never `git reset --hard`, never force-push, never `--no-verify`;
  - never touch git config or `.claude/settings.json`;
  - don't push;
  - commit only with explicit paths, after reading `git diff -- <paths>`.
- **Code style:**
  - use "sessions" in code;
  - no front-end timers: timing comes only from `BackendClock`'s ticks, apart from the probe's own timeout;
  - no test-only seams in production code;
  - every source file stays at 600 lines or fewer (the commit hook checks).
- **Off limits:**
  - never touch `dotnet/Assembly.Tests/Pinned/**`, ports 9797 or 9807, `~/assembly-dotnet` or `~/.local/share/assembly`;
  - don't start or stop any service;
  - kill only processes you started, by pid.
- **When a command fails:** if a required command fails, stop and report the exact error. Don't invent results.

## Report
For each item, give the commit hashes, the red run's failing test names and messages, and the green run's counts (filter, full suite, the 5-run loop). Also give the production and test line counts (`git diff --stat` from the base commit `b9ee6c9`), and anything in the note you found wrong, with file:line. Every claim needs a receipt (file:line, a hash, or the command that produced it).

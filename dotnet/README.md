# Assembly's backend

The .NET backend and window. It started as the TanStack AI spike's .NET backend (branch `spike/dotnet`) and was copied here at its commit cd097fc.

The Mac's backend listens on 127.0.0.1:18797, a remote's on 9797 (reached from the Mac on 19797). The Mac's data is in `~/assembly-dotnet/`, its token in `~/.config/assembly-dotnet/env`.

## Step 1: headless ACP client

`Assembly.Remote smoke` starts an ACP adapter and drives it through `initialize`, `session/new`, a turn that has to ask permission (writing a file), and a turn it cancels. Every JSON-RPC line is logged to a JSONL transcript. The checks print at the end, and the exit code is 0 only if all of them pass.

```sh
dotnet test
dotnet run --project Assembly.Remote -- smoke --log logs/smoke.jsonl -- \
  node <adapters>/node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js
```

On Workbox Test, from a checkout on the Mac:

```sh
dotnet/scripts/workbox.sh smoke    # one smoke run of the committed head; the transcript lands in dotnet/logs/
dotnet/scripts/workbox.sh record   # records the converter fixtures into Assembly.Tests/Fixtures/acp/
```

## Step 2: converter and row store

`Assembly.Remote rows <transcript.jsonl> [--db PATH]` replays a recorded transcript through the same path a live session takes:
ACP updates become our own events (`Acp/V1/UpdateReader`), the converter turns them into rows (`Rows/RowConverter`), and each
batch is committed to SQLite (`Store/RowStore`) before it would be pushed. It prints every row, what a page load returns, and how
the sizes compare with the raw ACP.

```sh
dotnet run --project Assembly.Remote -- rows Assembly.Tests/Fixtures/acp/tools.jsonl
```

The fixtures are in `Assembly.Tests/Fixtures/`: `acp/` was recorded on Workbox Test, and `handmade/` covers the two
shapes the recording never produced (see its README).

## Step 3: the API and the live hub

`Assembly.Remote serve -- <adapter command>` runs the backend the app talks to, on 127.0.0.1:9797. Every route the
virtual-int webview uses is here: the thread list and creation, pages of rows, tool details, send, stop, attachments,
and the `/live` socket. A send starts the thread's agent in its own clone of the repo (resuming the agent's session if it
has one), and each ACP update goes through the reader, the converter and one commit before it's pushed. A turn the
backend was killed in the middle of is closed on the next start.

```sh
dotnet run --project Assembly.Remote -- serve --home /tmp/assembly-home -- \
  node <adapters>/node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js
```

On Workbox Test, for the app on the Mac (its tunnel forwards 18797 and 18798 to the box's 9797):

```sh
dotnet/scripts/workbox.sh serve          # publishes the committed head and (re)starts the assembly-dotnet user unit
dotnet/scripts/workbox.sh kill-adapter   # the lost-run check: kills the backend's adapter mid-turn, by pid
dotnet/scripts/workbox.sh row-log latest # the row-position log: every row by seq, then the pages the app loads, checked
dotnet/scripts/workbox.sh stop
```

## Layout

Three projects. The engine is a library with no UI or Mac code; each host uses it, and neither host's code ships in the other.

- `Assembly.Engine/`: everything below except the commands.
- `Assembly.Remote/`: the host on the machine that runs the agent (Workbox): `serve`, plus the step 1 and 2 test benches (`smoke`, `record`, `rows`).
- `Assembly.Tests/`: the tests for both.

Inside the engine:

- `Acp/JsonRpc/`: JSON-RPC over newline-delimited JSON. One read loop and serialized writes.
- `Acp/`: the typed ACP v1 client, method names and the permission policy seam.
- `Acp/Messages/`: one record per ACP message we send or read, with source-generated JSON (AOT-safe).
- `Agents/`: starting, owning and stopping the adapter process.
- `Transcript/`: the JSONL log of every line, and its replay as events.
- `Events/`: what happens in a session, in our own terms. Only `Acp/V1/` knows ACP's shapes.
- `Rows/`: events to rows, one converter per session. `Rows/Payloads/` holds each row kind's JSON.
- `Store/`: the rows in SQLite: commit, rows after a seq, pages, tool details.
- `Live/`: the one door to the row store. Commits and socket subscriptions share its lock, so a socket sees each row once.
- `Threads/`: the sessions list (title, repo, the agent's own session id), in the same SQLite file.
- `Sessions/`: one host per thread: its agent, its converter, and the turn running now.
- `Attachments/`: images the composer attaches, stored by path for the agent to read.
- `Api/`: the routes, the token check and the live socket, on Kestrel.

In the remote host: `Serve/` (the step 3 command), `Replay/` (step 2's), `Record/` and `Smoke/` (step 1's harness).

It targets .NET 10 (LTS) for now, because the cloud machine can only install .NET 10. Moving to .NET 11 is a one-line change to `Directory.Build.props`.

On Workbox Test, `/proc/<pid>/cmdline` is readable by every user in the container, so the Claude Code session token (passed to the claude CLI as `--mcp-config`) is visible there to anyone who can log into the box.

## Upstream adapter bugs

- claude-agent-acp 0.94.0 answers `/context` twice: it forwards the result of a local-only command (`shouldForwardResult`) and only dedupes `/usage` and `/mcp`. The spike shows both answers rather than guessing which to drop.
- claude-agent-acp 0.94.0 reports a guessed context size until a session's first turn ends: it infers one from the model's name (400,000 for `claude-opus-5-5`), then uses the real window from the turn's result (1,000,000) and caches it for later sessions. `used` is on one basis throughout, so the ring's "% left" jumps once (90% to 96% in one check). The page shows what the agent reports.

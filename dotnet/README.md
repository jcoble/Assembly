# Assembly's backend

The .NET backend and window. It started as the TanStack AI spike's .NET backend (branch `spike/dotnet`) and was copied here at its commit cd097fc.

The desktop backend listens on 127.0.0.1:18797, a remote's on 9797 (reached from the desktop on 19797). The desktop's data is in `~/assembly-dotnet/`, its token in `~/.config/assembly-dotnet/env`.

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
virtual-int webview uses is here: the session list and creation, pages of rows, tool details, send, stop, attachments,
and the `/live` socket. A send starts the session's agent in its own clone of the repo (resuming the agent's session if it
has one), and each ACP update goes through the reader, the converter and one commit before it's pushed. A turn the
backend was killed in the middle of is closed on the next start.

```sh
dotnet run --project Assembly.Remote -- serve --home /tmp/assembly-home -- \
  node <adapters>/node_modules/@agentclientprotocol/claude-agent-acp/dist/index.js
```

On Workbox Test, for the desktop app (its tunnel forwards 18797 and 18798 to the box's 9797):

```sh
dotnet/scripts/workbox.sh serve          # publishes the committed head and (re)starts the assembly-dotnet user unit
dotnet/scripts/workbox.sh kill-adapter   # the lost-run check: kills the backend's adapter mid-turn, by pid
dotnet/scripts/workbox.sh row-log latest # the row-position log: every row by seq, then the pages the app loads, checked
dotnet/scripts/workbox.sh stop
```

## Layout

The core is in layers, and everything else is one folder per feature (plan section 4). `ArchitectureTests` checks the
rules.

- `Assembly.Domain/`: records several features share, and nothing else: agent events (`Events/`), row kinds and
  payloads (`Rows/`), session records (`Sessions/`) and the markdown block records (`Markdown/`). It references nothing.
- `Assembly.Application/`: references Domain only, with no SQLite, ASP.NET Core or process starting.
  - `Common/`: shared contracts and the ACP code. `Database/` (the plain-SQL contracts), `Acp/` (JSON-RPC, the typed
    ACP v1 client, one record per message, and `V1/`, the only place that knows ACP's shapes and provider quirks),
    `Agents/` (launching contracts and the shared adapter) and `Machines/`.
  - `Features/Chat/`: sessions, rows (with the live hub and the markdown parse), helpers and attachments.
  - `Features/Preferences/`. Diagnostics has no folder here until B8: its code makes native calls and reads files,
    so it's all in Infrastructure's `Diagnostics/`.
- `Assembly.Infrastructure/`: references Application. SQLite and the one writer (`Persistence/`), starting agent
  processes and git (`Agents/`), the local and remote machines (`Machines/`, `Remote/`), the transcript replay, `/mcp`,
  the files behind chat and diagnostics (`Chat/`, `Diagnostics/`), and today's HTTP routes (`Http/`) until the socket
  replaces them.
- `Assembly.Desktop/`: the desktop host, next to the window. `Assembly.Remote/`: the host on the machine that runs the agent (Workbox):
  `serve`, plus the step 1 and 2 test benches (`smoke`, `record`, `rows`).
- `Assembly.Tests/`: one project. `Architecture/`, `Core/`, `Infrastructure/` and `Features/<Feature>/` follow the
  code; `Draft/`, `Pinned/` and `Fixtures/` stay where they were.

It targets .NET 10 (LTS) for now, because the cloud machine can only install .NET 10. Moving to .NET 11 is a one-line change to `Directory.Build.props`.

On Workbox Test, `/proc/<pid>/cmdline` is readable by every user in the container, so the Claude Code session token (passed to the claude CLI as `--mcp-config`) is visible there to anyone who can log into the box.

## Upstream adapter bugs

- claude-agent-acp 0.94.0 answers `/context` twice: it forwards the result of a local-only command (`shouldForwardResult`) and only dedupes `/usage` and `/mcp`. The spike shows both answers rather than guessing which to drop.
- claude-agent-acp 0.94.0 reports a guessed context size until a session's first turn ends: it infers one from the model's name (400,000 for `claude-opus-5-5`), then uses the real window from the turn's result (1,000,000) and caches it for later sessions. `used` is on one basis throughout, so the ring's "% left" jumps once (90% to 96% in one check). The page shows what the agent reports.

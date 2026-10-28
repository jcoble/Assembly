# ACP cancel probe: does an adapter stop a request it's told to cancel? (2026-10-27)

This note records why only an explicit Stop reaches the agent, and why a load cancelled after it was sent is closed once
its late answer arrives (plan 5.3, C3, PR #78).

## What was run
A small script spoke ACP over stdio straight to each adapter in the base lane's agents file. No Assembly host sat in
between, and nothing was downloaded. These adapters answered `initialize`:
- `@agentclientprotocol/claude-agent-acp` 0.94.0
- `@agentclientprotocol/codex-acp` 2.2.2

For each adapter, in order:
1. `initialize`, then `session/new` with no MCP servers, in an empty scratch folder.
2. `session/set_config_option` (request id 10), changing `mode` to a value other than its current one, followed at
   once by the notification `$/cancel_request {"requestId": 10}`.
3. `session/prompt` (id 20) "Reply with the word ok", then, once the turn was running, `_session/steering` (id 21),
   followed at once by `$/cancel_request {"requestId": 21}`.
4. `session/close` (id 30) for the session.

Each adapter's process group was then stopped by its pid. No process command lines, arguments or environment were
recorded. No tokens appear in the raw lines, which were kept with the lane's notes, outside the repo.

## What came back

| | claude-agent-acp 0.94.0 | codex-acp 2.2.2 |
|---|---|---|
| `set_config_option` after its cancel | answered normally; mode changed `default` → `acceptEdits` | answered normally; mode changed `agent` → `read-only` |
| `_session/steering` after its cancel | answered `{"outcome":"injected"}` | answered `{"outcome":"injected"}` |
| `session/prompt` (the turn) | `stopReason: end_turn` | `stopReason: end_turn` |
| an error for the unknown `$/cancel_request` | none | none |
| `agentCapabilities.sessionCapabilities.close` | advertised | advertised |
| `session/close` | answered `{}` | answered `{}` |

One caveat: claude's `set_config_option` answered 40 ms after it was sent, so its cancel may have arrived after the
change had already run. The steer gives the same result on both adapters, though.

## Conclusions
- Neither adapter honours `$/cancel_request`. A cancelled request runs to its normal answer: a config change is applied
  and a steer is injected. So the backend sends no request-level cancel.
- Only `session/cancel`, sent by an explicit Stop, reaches the agent. A socket cancel or the 30 s call limit only stops
  our wait.
- Both adapters support `session/close`, and it answers `{}`.
- **Not tested:** whether a `session/close` sent while a `session/new` or `session/resume` is still loading closes the
  session being loaded. Because that's unknown, a load cancelled after it was sent waits for its late answer, up to
  the close limit, and only then closes the session the agent opened. That is the same for `session/new` and
  `session/resume` (`SessionOpener.OpenOnAsync`).

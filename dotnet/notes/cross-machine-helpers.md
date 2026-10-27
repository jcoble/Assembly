# Helper sessions across machines (item 7, commit 3)

The desktop reaches Workbox Test over its API; Workbox Test never reaches the desktop. That stays true.

## desktop session → Workbox Test

- `start_session` takes an optional `machine`: one of the machines the caller's machine shows (on the desktop,
  "workbox-test"; on the box, only itself). The desktop creates the helper's session on the box with
  `POST /sessions {agent, repoUrl, parentSessionId, helper: true, title}` and keeps its usual copy of it
  (machine, parent, helper). The prompt goes through the box's send route, like any remote send.
- `send_message` to a box session the caller can reach goes the same way: through that session's machine.
- The helper's rows are copied to the desktop as for any remote session, so `message_status` on the desktop reads the reply
  from the desktop's copy. The helper row in the parent goes idle when the copied turn ends, and the parent's session
  stays open while a remote helper's copied run is open.
- Stop on a remote helper's row stops its run on the box.

## Workbox Test session → desktop session

- A box helper started by a desktop session can message its parent. The box doesn't have the parent's session, so the
  message is queued: kept in the box's `helper_messages` with no run, as "working".
- While a box helper of one of its sessions has a run open, the desktop asks the box every second for queued messages
  (`GET /outbox`). It delivers each to its desktop session (a prompt, or steered into the running turn), and keeps its own
  record of it. Then it reports back with `POST /outbox/:id {runId}` or `{error}`.
- When that desktop run ends, the desktop posts the answer: `POST /outbox/:id/answer {state, reply, error}`. The box stores
  it, and the box helper's `message_status` reads it.

## Limits

- The same limits apply: 5 helpers at work and 20 messages between any two sessions.
- A box session can't start a helper on the desktop, and the box only lists its own sessions.

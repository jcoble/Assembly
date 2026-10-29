# Remote over the socket: behavior list

Draft by Claude, not corrected by Jesse. Drafted 2026-10-29 from Jesse's recorded decisions, the plan (section 5, phases C2 and P1) and the step-3 recon (docs/plans/2026-10-29/remote-socket-recon.md); the old app is reference only.

This list covers connecting the desktop to remote machines, using their sessions, and exchanging messages between machines.
It covers the same behaviour for users, agents and workflows; the phone client is outside this scope.

## Connecting

- remote#1: Settings shows saved remote machines and lets the user add or remove one while the app is running. (Jesse, 10-28)
- remote#2: The desktop automatically connects to saved machines when it starts. (Jesse, 10-24)
- remote#3: A saved machine that is down at launch does not prevent the desktop opening or local sessions working.
- remote#4: A saved machine that is down at launch is retried on the backend clock until it connects or the user disconnects or removes it. (Jesse, 10-29)
- remote#5: The user can connect or disconnect a machine without restarting the app.
- remote#6: Each machine shows whether it is connecting, connected or unreachable.
- remote#7: A dropped link is noticed and shown against the machine and its sessions.
- remote#8: After a dropped link, reconnect attempts use the backend clock; session and message changes arrive as pushes, without polling.
- remote#9: Connect lets the user request another connection attempt immediately.
- remote#10: The connection token is never displayed or included in logs.
- remote#11: Incompatible builds are refused with a message naming the machine and explaining the incompatibility; different build dates alone do not make builds incompatible. (assumed)

## Sessions on a machine

- remote#12: From the desktop, a user or agent can create a session in a folder on a connected remote machine. (Jesse, 10-27)
- remote#13: Sessions created on a remote machine appear alongside local sessions in the desktop's session list, even when created somewhere other than that desktop. (Jesse, 10-29)
- remote#14: Remote sessions and their project groups are labelled with their machine.
- remote#15: The same folder on the Mac and Workbox Test appears as two projects in the rail, labelled by machine. (Jesse, 10-29)
- remote#16: A remote session's title and state update as promptly as a local session's, including Working, Needs you, failed and an unseen completed turn.
- remote#17: Questions and permission requests from a remote session appear on the desktop and keep Needs you current until answered.
- remote#18: An open remote chat streams new rows in order while its turn runs.
- remote#19: Older remote chat history loads in pages from the desktop's saved copy.
- remote#20: After reconnecting, remote chats catch up with missed rows in order, without missing or repeating rows.
- remote#21: A turn started directly on the remote machine updates the desktop's session state and streams into its open chat.

## Sending and stopping

- remote#22: Sending a prompt from the desktop starts the turn on the session's machine.
- remote#23: Stop reaches the remote session and stops its running turn.
- remote#24: An answer to a question or permission request reaches the remote session; an answer that is no longer accepted reports that to the caller.
- remote#25: Stopping a background task or helper reaches that task on the remote machine.
- remote#26: Cancelling a remote read or replacing it with a newer read stops the unwanted work on the remote machine and prevents its late result appearing.
- remote#27: Cancelling a request or losing its connection does not itself stop an agent's turn; stopping the turn requires Stop.
- remote#28: An operation that cannot reach its machine answers “unreachable” and names the machine.
- remote#29: A save accepted for writing finishes even if the caller disconnects; success means the save committed.
- remote#30: Reconnecting does not automatically repeat a prompt, session creation or interrupted attachment upload.

## Large results and attachments

- remote#31: Remote tool output and diffs have the same size caps as local output and diffs.
- remote#32: Large tool results, history pages and attachment transfers do not hold up live rows, status changes or ordinary calls.
- remote#33: A remote session accepts supported attachments up to 20 MB and refuses anything larger. (Jesse, 10-26)
- remote#34: Attachments travel in chunks of at most 1 MB and become available to the session through one atomic commit. (Jesse, 10-26)
- remote#35: An upload abandoned before its commit leaves no partial attachment linked to the session.
- remote#36: An attachment can be read back from the remote machine in chunks without blocking live updates.

## Messages between sessions and machines

- remote#37: Sessions can exchange Assembly messages across connected machines in both directions. (Jesse, 10-24)
- remote#38: A remote session's message to a desktop session is pushed to its recipient without polling.
- remote#39: The sender receives the delivery result, including the reason when delivery fails.
- remote#40: Replies and the result of the recipient's turn return to the sending session as pushed updates, without polling.
- remote#41: Pending messages, delivery results and replies survive a dropped link and resume after reconnecting without loss or duplicate delivery. (assumed)
- remote#42: A result sent back to the session that started remote work arrives as an Assembly message. (Jesse, 10-28)
- remote#43: Disconnecting or closing the desktop does not stop work already running on the remote machine. (Jesse, 10-24)

## Providers

- remote#44: Every behaviour above holds for Claude Code and Codex alike, using the providers installed and signed in on the session's machine.

## Where the sources disagree

- **Startup retries:** the saved-machines branch makes one startup attempt and does not retry a machine that never connected. This draft follows Jesse's supplied 10-29 decision to retry on the backend clock. Receipts: `docs/port-product-decisions.md`, “Sessions and machines”; `docs/plans/2026-10-26/implementation-plan.md:1774`; recon report, “Auth and reconnect behavior.”
- **Remote freshness:** the plan leaves remote summary freshness as a separate decision, and the existing path refreshes metadata after a followed turn. This draft follows the requested scope: remote titles and state stay as fresh as local ones, including externally started turns. Receipts: `docs/plans/2026-10-26/implementation-plan.md:1510`; recon report, “Gaps and socket equivalents.”
- **Polling and reconnecting:** existing outbox delivery polls, and the sessions-list draft specifies repeated connection checks. This draft follows C2 and P1: pushed changes and backend-clock retries. Receipts: `docs/behavior/sessions-list.md`, `sessions-list#12`; `docs/behavior/g0-socket.md`, `g0-socket#8`; `docs/plans/2026-10-26/implementation-plan.md:1763` and `:1982`.
- **Deleting shared routes:** C2 says to delete the old remote chat routes, while the frozen-page note retains routes needed by the old page until its removal. This behaviour list does not choose deletion sequencing. Receipts: `docs/plans/2026-10-26/implementation-plan.md:1447` and `:1772`; recon report, “Risks.”
- **Compatibility:** the existing path refuses incompatible stored row formats, but the planned socket greeting does not specify a replacement check. This draft preserves the visible refusal without choosing its implementation. Receipts: `docs/plans/2026-10-26/implementation-plan.md:389`; recon report, “Gaps and socket equivalents,” version-check entry.

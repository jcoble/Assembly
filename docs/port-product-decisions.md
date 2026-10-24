# Jesse's decisions for the port

Quotes are Jesse's own words from Oct 24, 2026 (times UTC), kept as he wrote them. Lines without quotes are summaries of what he chose. Anything not here is not decided by him.

## Goals
- "it doesn't have to do everything the same, just work, low mem, understandable and extensible."
- 23:10: "Nothing has been stable for months." The spec for each feature is the behavior list he corrects, not the old code.
- Code is organized by feature, one class per file, small classes with one job each.

## Shape
- Host, 22:12: "we get rid of the tauri listeners first off. Or just use them for the native stuff if we have to." 22:13, to a thin Tauri shell (window, browser tabs, native calls, no app listeners) with a separate .NET backend over a websocket and the front end ported feature by feature: "I'll go w that."
- Chat: option B on a decision card (07:59). The machine running the agent turns its output into display rows once and saves them; the Mac copies remote rows by sequence number; the page only draws.
- Rows, 17:07: "i decided i want to keep the rows how we had them". 17:09: "But I want to do everything else through the acp adapter though".
- ACP v1 first: "We don't want to make this port too complicated".

## Sessions and machines
- 20:41: "agent sessions should just stay open as long as there is no subagents, (orchestrators - stay open if they have their helpers running, and stay open if workflows they are delegating are open) and they are done with their turn."
- Rewind: "we can hold on". Fork: "only if it's not hard".
- Remote work: workflows and pushes "def should keep running if on the remote"; with the laptop closed it "shouldn't matter too much". Local work while the laptop is closed: "Maybe that could be a setting?" (not decided).
- "I need the agents to be able to communicate across devices".
- "Auto connect is a requirement." "It's just loading the remote connection up on app start."
- Language servers for local and remote projects: "this was supposed to be both".
- Codex: "The plugins should be loaded"; agents need MCP servers, "at lesat the basic ones". Antigravity stays.
- Existing data "does not matter at all" (no migration).
- Session handoff between machines: "would love to have them but they are not a big concern as of now."

## Features
- "Have to get rid of the workflows panel, but not workflows themselves, it got moved to a big window". "Inbox tab i think goes. And the rest of everything stays."
- Build Board, 20:50: "That Build Board has to be taken out for the time being if it's what's making theUI as laggy as it is..Sometimes its literally 5s before my click does anything."
- Messages "need a litte UI work but are good". Remote Run comes later ("not huge").
- Browser: "pretty darn important to me". "Clipboard is iimportant too for sure". Notifications are lower priority. Keychain and packaging are needed.

## How the work runs
- 22:49: "I think we do 3 features at a time". 22:51: "i have the whole workbox 32gb ram and 800gbs of space. That's the place to do a lot of it."
- 23:01: "The remotes should be tested in the workbox-test container though so it's like they have a remote machine to test with"
- 23:06: "i'll get a couple Codex's on the backend, getting failing tests going on the .NET side, to get the intended behavior pinned, ad then have them start to port it over. Clean code style."
- 23:09: "the intended behavior is supposed to be prod and been working for a while. Not where a lot of it is wrong or i haven't even tested it yet."
- Every change is checked four ways (Claude Code and Codex, on the Mac and on Workbox Test), with screenshots before and after.

## Open, not decided by Jesse
- A keep-awake setting for local work while the laptop is closed.
- Which of his own MCP servers each agent session loads.
- Where drafts live for phone use, and where file context-menu actions run (defaults in the front-end plan: on the machine running the session, and on the machine with the files).

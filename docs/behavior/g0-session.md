# Session view: behavior list

Draft by Claude, not corrected by Jesse.

g0-session#1 Opening a session shows its newest messages at once, then keeps up with new ones as they arrive. Switching to another before they load cancels that load on the backend, and none of them show in the next session.
g0-session#2 Scrolling up loads older messages before you reach the top, and the message you're reading stays where it is.
g0-session#3 History has no blank gaps, no repeated messages and no empty rows, also where a resumed session joins its earlier history.
g0-session#4 A reply streams in place, its text coming in smoothly like typing, in one message, never as a new message per piece. It speeds up the further it falls behind, so even a long burst shows within two seconds, and it stops moving once it has caught up.
g0-session#5 Sending a message shows it at once as pending, until the session's own copy of it arrives, and scrolls it to the top of the view with the reply writing below it. Partly built: the prompt shows once the session's copy arrives (nothing pending shows before then), and then goes to the top. Known limit: a reader who scrolls before the prompt arrives is still moved to the top.
g0-session#6 "Jump to latest" goes to the newest message and keeps following while a turn writes; scrolling yourself stops following. Following never runs past the prompt.
g0-session#7 Going back to the latest keeps the older messages already loaded.
g0-session#8 Each session keeps its reading place and its unsent draft when you switch away and back while the app runs. The reading place changes only when you scroll. Known limit: the place comes back only when it's within the newest page of messages; a place further back opens at the newest messages instead.
g0-session#9 A finished turn folds its work behind one "Worked for 42s · Edited 2 files, ran 3 commands" heading above its final reply; opening it keeps the heading where it was. A running turn never folds.
g0-session#10 Under each turn: how long it worked, when it started, the model, tokens, cache and cost. "Stopped" when you stopped it (once, even if another turn's rows split it), "Failed" when it failed.
g0-session#11 "Checkout changed · main@1a2b3c4 → feat@5d6e7f8" shows above a prompt when the run started on a different branch or commit.
g0-session#12 While a turn runs, its tool calls show as one card at the foot of the timeline: a Working pill, a 28 px line per step with how long it ran (the running step shimmering, its time counting up), and "3 steps · 0:41" under them. When the turn ends its work folds under "Worked for…". Nothing moves when nothing is running.
g0-session#13 A tool call shows its title, summary and line counts; opened, its command, the end of its output and its diff. A long diff shows its first lines until you ask for all of it. Output cut by the backend says so.
g0-session#14 An opened tool call reads its detail once, and again only when the call's state changes; an edited file's diff shows once the edit finishes.
g0-session#15 What you opened (a tool call, thinking, a fold, a long prompt) stays open when you scroll away and back.
g0-session#16 A permission or question from the agent shows as a card you answer in place; once answered it shows the answer.
g0-session#17 Background work shows its state and a Stop button while it runs; a subagent shows its title and state, has a Stop button while it runs, and opens its own session on click.
g0-session#18 The agent's plan shows above the composer, with the step in progress marked.
g0-session#19 A failed run says what went wrong in a sentence, not raw JSON; "The conversation is full" says what to do. A run you stopped shows no error.
g0-session#20 A prompt from another session is labelled with that session (its title, agent and machine) and opens it on click; a session another session started links to that session in its header, and takes prompts like any session.
g0-session#21 A prompt longer than ten lines folds until opened. Each prompt and final reply has a copy button and its time ("Thursday 12:02 PM", or "Oct 17, 12:02 PM" past a week).
g0-session#22 Enter sends, Shift-Enter adds a line. While a turn runs, sending steers it and Stop stops it.
g0-session#23 A new session's first message sends.
g0-session#24 Images attach by drop, paste or picker (png, jpeg, gif or webp, up to 20 MB) and show as chips you can remove; send waits until each has uploaded. Other dropped files go into the draft as their paths. Not built yet.
g0-session#25 Attached images show on the sent message and open full size; a click or Esc closes it. Partly built: a sent message's images open full size, but nothing here attaches images yet.
g0-session#26 Typing "/" lists the agent's own commands (names starting with what you typed first); a pick fills the draft and selects its hint. Not built yet.
g0-session#27 The agent's own menus (model, effort, mode) sit in the composer, open upward, and show the agent's current value after each turn. Not built yet.
g0-session#28 A ring in the composer shows how much of the context window is left, warm at a quarter left and hot at a tenth. Not built yet.
g0-session#29 A remote session opens its saved messages while its machine is unreachable, and says it's offline; a backend restart in the middle of a turn doesn't leave "Working" on.
g0-session#30 The sessions column lists every session, newest first, and loads more as you scroll; picking one opens it. A working session gets a still dot once the list knows which are working.
g0-session#31 A new session is started from the sessions column by picking its agent, machine and folder, and opens ready for its first message.
g0-session#32 The composer draws no spellcheck underlines.
g0-session#33 Dragging in the space beside the messages doesn't select the text above it.
g0-session#34 Small animations wherever they help: a new row fades in; a fold, tool call, thinking or question grows in place when opened (the row grows over 160 ms and the rows below slide down, its content fading in, on a lighter band) and shrinks back when shut or answered, with its chevron turning; a plan step's checkmark appears; Stop shows while a turn runs; an upload's chip shows its progress (not built: nothing attaches yet, #24). Each is short, ends by itself, never delays what a click does, and is instant with Reduce Motion on.

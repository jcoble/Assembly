# G0 shape: behavior list

Draft by Claude, not corrected by Jesse.

The GPUI window's shape: the sessions column, the center, the tools drawer and the bottom dock on the GPUI kit's
DockArea, the top row with the Home button, and the utility strip. Sources: notes/gpui-g0/g0-lanes.md (lane g0-shape)
and notes/gpui-g0/look-and-behavior-carryovers.md section 2, with the old app's commits. Settings and the palette are
g0-host's; what each pane shows belongs to the lane that owns that pane.

g0-shape#1 The window opens in the old app's layout. The left column is one tabbed column, Sessions, Files and Search,
and Chat is in the center (Jesse 2026-10-27, look item 20). The drawer is on the right with Source control, Agents,
Tasks, Worktrees, Run, Context and History, and it's closed at launch. The bottom dock holds Problems and Terminal, and it's closed too.
g0-shape#2 Any pane can be dragged anywhere, and any divider can be resized.
g0-shape#3 The layout is kept across relaunches, including the sessions column's width and whether it's folded
(491a4447, 5bfc3360). A layout saved by an older build, or one that can't be read, opens the default layout instead.
So does a launch with no backend, or one whose backend never gets ready: the window is never left empty.
g0-shape#4 Launching the app doesn't save the layout. Changes made together save once, and a save is never queued
behind older ones (378bc67d).
g0-shape#5 Opening the drawer pushes the panes over instead of covering them. Closing it gives the center its width
back (289c449b).
g0-shape#6 The round Home button, with the old app's house icon, sits above the sessions column and keeps its width,
so the dividers line up with the top row. It follows the column while the column's edge is dragged (f4898b79,
668f7350).
g0-shape#7 The drawer's tabs, Sessions, Chat and Problems have no close button (951f2b43).
g0-shape#8 A pane shows nothing until the lane that owns it fills it, and nothing hidden is mounted (6353dc4d).
g0-shape#9 The utility strip at the foot of the sessions column has Resources and Usage (there's no bottom bar), so
folding the sessions column hides them too. Each opens a popover anchored to it, on screen and above the strip
(fa39c659, 36abf71a, a56526ec). Nothing in the strip polls.
g0-shape#10 A row or card reveals more by growing in place, never as an accordion (Jesse 2026-10-27, look items 13
and 16). A click on its title grows it to fit its body, the title stays where it was, and a second click shrinks it
back; the body stays until the row has shrunk, then goes. A row that's open when it first shows is drawn open. A body
whose height changes while the row is open grows or shrinks to it the same way. Every row that grows in place uses
this one row (Jesse 2026-10-27, "many list items from diff places get the same behavior always"): its owner hears each
click and each frame's height, can open or shut it itself, and can draw the band and the body from its own state.
g0-shape#11 The sessions column can be dragged from 220 to 560 px wide and the drawer from 240 to 640 px (the approved
look); a dragged edge stops at the limit, and the drag still saves once, when it ends.
g0-shape#12 The side docks' tabs are the old app's (Jesse 2026-10-27, "make these more like the ones in the old app,
same with left side"). The drawer's are one rounded strip of icon pills, the old app's icons, the shown one lit and the
name on hover. The sessions column's are the old top tab row's text chips. A click shows that pane, a side tab still
drags its pane anywhere (g0-shape#2), and a row too narrow for its tabs scrolls. Chat and the bottom dock keep the
kit's tabs.
g0-shape#13 The drawer's Context tab lists the files the selected session's tool calls edited, deleted or moved, newest
first, each once with its file name, its folder and its lines added and removed (`rows/files`, parity map 11c). A
failed call touched nothing; a call that changed several files lists each with its own lines. The tab reads the list when it's shown and when another session is picked while it's
shown, and doesn't follow the live rows, as the old app's didn't, so a remote session picked before its rows are
copied here can list too few until the tab is shown again. With no session picked it says to pick one; a read that
fails or is cancelled says why.

g0-shape#14 Under Files touched, the Context tab lists the images the selected session's prompts carried, newest prompt
first, each by the name it was attached with (`rows/attachments`, parity map 11d: each prompt's "Attached image:"
lines, from this machine's copy of the session's rows). A click reads the image by its file name in the store and opens
it full size, as Chat's chips do; one that won't open says why. A remote session's images stay on its machine, so its
rows don't open and a note names the machine. With nothing attached it says images added to the message box are listed
there; a read that fails says why. It reads when Files touched does. To stay inside the socket's 4 MB answer, the read
sends only the end of each prompt, where the lines are, and the newest 50 prompts; when it leaves older ones out, the tab
says so.
g0-shape#15 The drawer's History tab lists the sessions the sessions list has loaded, by project (its folder or repo,
and its machine when it isn't this computer), the project with the newest session first (parity map 12a). Each
project starts folded, with its count; a click opens or folds it. Open, it lists its sessions newest first, 25 at a
time, with a "Show 25 more" line while more are loaded, each row with its title, agent and age ("now", "5m", "3h",
"2d", "1w", "1mo", "1y" by the backend clock, ticking only while the tab is shown). A click on a session opens it, as
the sessions column does. A line at the end loads the list's next older page while there is one. With no sessions it
says they appear here; before the backend starts it says it's waiting. Every session has its own checkout, so there's
no worktree level; sessions found in the agents' own folders, and resuming them, are parity map 12b to 12d.
